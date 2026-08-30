using GameTracker.Domain.Dtos;
using GameTracker.Domain.Entities;
using GameTrackerWpfClientApp.Data;
using GameTrackerWpfClientApp.Services.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace GameTrackerWpfClientApp.Services.Sync;

/// <summary>The outcome of a sync attempt, for display and for retry decisions.</summary>
/// <remarks>
/// <paramref name="Offline"/> is carried separately from <paramref name="Succeeded"/>
/// because the two call for different handling: an unreachable server is the expected
/// resting state of this client and needs no more than a quiet indicator, whereas a real
/// failure warrants a warning the user can act on.
/// </remarks>
public sealed record CatalogueSyncResult(
    bool Succeeded,
    int RowsApplied,
    long Cursor,
    string? Message,
    bool Offline = false)
{
    public static CatalogueSyncResult Skipped(string message) => new(false, 0, 0, message);

    public static CatalogueSyncResult OfflineResult(long cursor, int rowsApplied) =>
        new(false, rowsApplied, cursor, "The server could not be reached; working offline.", Offline: true);
}

/// <summary>
/// Pulls catalogue changes from <c>GET /api/sync/changes</c> into the local SQLite mirror.
/// </summary>
/// <remarks>
/// The cursor is the server-issued <c>ServerVersion</c>, never a timestamp, and it is
/// advanced only after the batch transaction commits. That ordering is the whole
/// correctness argument: if the process dies mid-sync, the cursor still points at the last
/// fully-applied page, so the next run re-requests the interrupted page rather than
/// skipping it. Advancing first would silently lose rows forever, because the server only
/// ever returns rows *above* the cursor.
/// </remarks>
public sealed class CatalogueSyncService
{
    /// <summary>
    /// One shared cursor, because the server pages Games, Cars and Tracks against a single
    /// version sequence and trims each page to a common cutoff.
    /// </summary>
    private const string CursorName = "Catalogue";

    private const int PageSize = 200;

    /// <summary>
    /// Stops a malformed <c>HasMore</c> from looping forever. At this page size it allows
    /// far more rows than the catalogue will ever hold.
    /// </summary>
    private const int MaxPagesPerRun = 200;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CatalogueSyncService> _logger;

    // Sync is triggered from several places (startup, a timer, a manual refresh button).
    // Two concurrent runs would share a cursor and race on the same rows, so overlapping
    // callers are turned away rather than queued: a second immediate sync has nothing new
    // to fetch anyway.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CatalogueSyncService(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<CatalogueSyncService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>True while a sync is running, so the UI can disable its refresh button.</summary>
    public bool IsSyncing { get; private set; }

    /// <summary>
    /// Fetches and applies every outstanding page. Returns without waiting if a sync is
    /// already in progress.
    /// </summary>
    public async Task<CatalogueSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return CatalogueSyncResult.Skipped("A catalogue sync is already running.");
        }

        IsSyncing = true;

        try
        {
            return await SyncCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            // Offline is the expected state for this application, not an error worth
            // surfacing loudly: recorded laps stay queued locally until connectivity returns.
            // The handler now converts transport faults into a 503, so reaching here means
            // a response arrived and was unusable -- still not fatal, but not offline either.
            _logger.LogWarning(ex, "Catalogue sync failed against a reachable server.");
            return new CatalogueSyncResult(false, 0, 0, "The sync request failed. See the log for details.");
        }
        finally
        {
            IsSyncing = false;
            _gate.Release();
        }
    }

    private async Task<CatalogueSyncResult> SyncCoreAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(ApiClientNames.GameTrackerApi);

        var totalApplied = 0;
        long cursor;

        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ClientDbContext>();
            cursor = await GetCursorAsync(context, cancellationToken);
        }

        for (var page = 0; page < MaxPagesPerRun; page++)
        {
            var response = await client.GetAsync(
                $"api/sync/changes?since={cursor}&take={PageSize}",
                cancellationToken);

            if (AuthenticationHandler.IsOffline(response))
            {
                // Pages already applied are kept: the cursor was committed with them, so
                // the next run resumes from exactly here rather than starting over.
                _logger.LogInformation(
                    "Catalogue sync stopped at cursor {Cursor} because the server is unreachable.",
                    cursor);

                return CatalogueSyncResult.OfflineResult(cursor, totalApplied);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The authentication handler has already cleared the token and signed out;
                // there is nothing useful to retry here.
                return new CatalogueSyncResult(false, totalApplied, cursor, "Sign-in is required.");
            }

            response.EnsureSuccessStatusCode();

            var changes = await response.Content.ReadFromJsonAsync<SyncChangesResponse>(cancellationToken)
                ?? throw new InvalidOperationException("The sync endpoint returned an empty body.");

            // A page can legitimately be empty when the client is already up to date.
            if (changes.NextVersion <= cursor && !changes.HasMore)
            {
                break;
            }

            // Each page is applied in its own transaction rather than accumulating the whole
            // sync in one: a long-running write transaction would block local recording
            // writes to the same SQLite file for the duration of a full catalogue pull.
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ClientDbContext>();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                var applied = await ApplyPageAsync(context, changes, cancellationToken);

                // The cursor is written inside the same transaction as the rows, so the two
                // can never disagree.
                await SetCursorAsync(context, changes.NextVersion, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                totalApplied += applied;
            }

            cursor = changes.NextVersion;

            if (!changes.HasMore)
            {
                break;
            }
        }

        _logger.LogInformation(
            "Catalogue sync applied {RowCount} row(s); cursor now at {Cursor}.",
            totalApplied,
            cursor);

        return new CatalogueSyncResult(true, totalApplied, cursor, null);
    }

    private static async Task<int> ApplyPageAsync(
        ClientDbContext context,
        SyncChangesResponse changes,
        CancellationToken cancellationToken)
    {
        var applied = 0;

        // Games are upserted first: cars and tracks in the same page reference them, and a
        // child row inserted before its parent would violate the foreign key.
        foreach (var dto in changes.Games)
        {
            var existing = await context.Games.FindAsync([dto.Id], cancellationToken);

            if (dto.IsDeleted)
            {
                // Children are removed explicitly because the catalogue relationships are
                // not configured to cascade locally.
                if (existing is not null)
                {
                    var orphanedCars = await context.Cars
                        .Where(c => c.GameId == dto.Id)
                        .ToListAsync(cancellationToken);
                    var orphanedTracks = await context.Tracks
                        .Where(t => t.GameId == dto.Id)
                        .ToListAsync(cancellationToken);

                    context.Cars.RemoveRange(orphanedCars);
                    context.Tracks.RemoveRange(orphanedTracks);
                    context.Games.Remove(existing);
                    applied++;
                }

                continue;
            }

            if (existing is null)
            {
                context.Games.Add(new Game
                {
                    Id = dto.Id,
                    Name = dto.Name,
                    ShortName = dto.ShortName,
                    ServerVersion = dto.ServerVersion
                });
            }
            else
            {
                existing.Name = dto.Name;
                existing.ShortName = dto.ShortName;
                existing.ServerVersion = dto.ServerVersion;
            }

            applied++;
        }

        // Save the games now so the cars and tracks below can satisfy their foreign key
        // within this same transaction.
        await context.SaveChangesAsync(cancellationToken);

        foreach (var dto in changes.Cars)
        {
            var existing = await context.Cars.FindAsync([dto.Id], cancellationToken);

            if (dto.IsDeleted)
            {
                if (existing is not null)
                {
                    context.Cars.Remove(existing);
                    applied++;
                }

                continue;
            }

            if (existing is null)
            {
                context.Cars.Add(new Car
                {
                    Id = dto.Id,
                    GameId = dto.GameId,
                    ExternalId = dto.ExternalId,
                    Name = dto.Name,
                    Manufacturer = dto.Manufacturer,
                    Class = dto.Class,
                    Year = dto.Year,
                    ServerVersion = dto.ServerVersion
                });
            }
            else
            {
                existing.GameId = dto.GameId;
                existing.ExternalId = dto.ExternalId;
                existing.Name = dto.Name;
                existing.Manufacturer = dto.Manufacturer;
                existing.Class = dto.Class;
                existing.Year = dto.Year;
                existing.ServerVersion = dto.ServerVersion;
            }

            applied++;
        }

        foreach (var dto in changes.Tracks)
        {
            var existing = await context.Tracks.FindAsync([dto.Id], cancellationToken);

            if (dto.IsDeleted)
            {
                if (existing is not null)
                {
                    context.Tracks.Remove(existing);
                    applied++;
                }

                continue;
            }

            if (existing is null)
            {
                context.Tracks.Add(new Track
                {
                    Id = dto.Id,
                    GameId = dto.GameId,
                    ExternalId = dto.ExternalId,
                    Name = dto.Name,
                    LayoutName = dto.LayoutName,
                    Country = dto.Country,
                    LengthMetres = dto.LengthMetres,
                    ServerVersion = dto.ServerVersion
                });
            }
            else
            {
                existing.GameId = dto.GameId;
                existing.ExternalId = dto.ExternalId;
                existing.Name = dto.Name;
                existing.LayoutName = dto.LayoutName;
                existing.Country = dto.Country;
                existing.LengthMetres = dto.LengthMetres;
                existing.ServerVersion = dto.ServerVersion;
            }

            applied++;
        }

        return applied;
    }

    private static async Task<long> GetCursorAsync(ClientDbContext context, CancellationToken cancellationToken)
    {
        var metadata = await context.SyncMetadata
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.EntityName == CursorName, cancellationToken);

        // Absent cursor means a first run: 0 requests the entire catalogue.
        return metadata?.LastSyncedVersion ?? 0;
    }

    private static async Task SetCursorAsync(
        ClientDbContext context,
        long version,
        CancellationToken cancellationToken)
    {
        var metadata = await context.SyncMetadata
            .FirstOrDefaultAsync(m => m.EntityName == CursorName, cancellationToken);

        if (metadata is null)
        {
            metadata = new SyncMetadata { EntityName = CursorName };
            context.SyncMetadata.Add(metadata);
        }

        metadata.LastSyncedVersion = version;
        metadata.LastSyncedAtUtc = DateTime.UtcNow;
    }
}
