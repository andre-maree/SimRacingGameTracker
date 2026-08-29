using GameTracker.Domain.Dtos;
using GameTrackerBlazorServerApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerBlazorServerApp.Controllers;

/// <summary>
/// Incremental catalogue sync for offline clients.
/// </summary>
/// <remarks>
/// The cursor is the server-issued <c>ServerVersion</c>, never a timestamp: client clocks
/// are wrong often enough (timezones, manual changes, VM snapshots) that time-based sync
/// silently drops rows. Deleted rows are returned as tombstones so clients can purge them.
/// </remarks>
[ApiController]
[Route("api/sync")]
[Authorize(Policy = "UserOrAdmin")]
public sealed class SyncController(ApplicationDbContext context) : ControllerBase
{
    private const int MaxTake = 500;
    private const int DefaultTake = 100;

    /// <summary>
    /// Returns every catalogue row changed after <paramref name="since"/>, oldest first.
    /// </summary>
    /// <param name="since">The client's last committed <c>ServerVersion</c>. 0 for a full sync.</param>
    /// <param name="take">Page size, capped at <see cref="MaxTake"/>.</param>
    [HttpGet("changes")]
    [ProducesResponseType<SyncChangesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SyncChangesResponse>> GetChanges(
        [FromQuery] long since = 0,
        [FromQuery] int take = DefaultTake,
        CancellationToken cancellationToken = default)
    {
        if (since < 0)
        {
            return BadRequest("since must be zero or greater.");
        }

        take = Math.Clamp(take, 1, MaxTake);

        // Each table is paged independently, then reconciled onto a single cursor below.
        var games = await context.Games.AsNoTracking()
            .Where(g => g.ServerVersion > since)
            .OrderBy(g => g.ServerVersion)
            .Take(take)
            .Select(g => new GameSyncDto
            {
                Id = g.Id,
                Name = g.Name,
                ShortName = g.ShortName,
                ServerVersion = g.ServerVersion,
                IsDeleted = g.IsDeleted
            })
            .ToListAsync(cancellationToken);

        var cars = await context.Cars.AsNoTracking()
            .Where(c => c.ServerVersion > since)
            .OrderBy(c => c.ServerVersion)
            .Take(take)
            .Select(c => new CarSyncDto
            {
                Id = c.Id,
                GameId = c.GameId,
                ExternalId = c.ExternalId,
                Name = c.Name,
                Manufacturer = c.Manufacturer,
                Class = c.Class,
                Year = c.Year,
                ServerVersion = c.ServerVersion,
                IsDeleted = c.IsDeleted
            })
            .ToListAsync(cancellationToken);

        var tracks = await context.Tracks.AsNoTracking()
            .Where(t => t.ServerVersion > since)
            .OrderBy(t => t.ServerVersion)
            .Take(take)
            .Select(t => new TrackSyncDto
            {
                Id = t.Id,
                GameId = t.GameId,
                ExternalId = t.ExternalId,
                Name = t.Name,
                LayoutName = t.LayoutName,
                Country = t.Country,
                LengthMetres = t.LengthMetres,
                ServerVersion = t.ServerVersion,
                IsDeleted = t.IsDeleted
            })
            .ToListAsync(cancellationToken);

        // The three tables share one cursor, so the page must stop at the lowest version
        // any truncated table reached. Publishing a higher cursor would skip the rows the
        // other tables still owe the client, and they would never be requested again.
        var truncatedCutoffs = new List<long>();

        if (games.Count == take)
        {
            truncatedCutoffs.Add(games[^1].ServerVersion);
        }

        if (cars.Count == take)
        {
            truncatedCutoffs.Add(cars[^1].ServerVersion);
        }

        if (tracks.Count == take)
        {
            truncatedCutoffs.Add(tracks[^1].ServerVersion);
        }

        var hasMore = truncatedCutoffs.Count > 0;

        if (hasMore)
        {
            var cutoff = truncatedCutoffs.Min();
            games.RemoveAll(g => g.ServerVersion > cutoff);
            cars.RemoveAll(c => c.ServerVersion > cutoff);
            tracks.RemoveAll(t => t.ServerVersion > cutoff);
        }

        var nextVersion = new[]
        {
            games.Count > 0 ? games[^1].ServerVersion : since,
            cars.Count > 0 ? cars[^1].ServerVersion : since,
            tracks.Count > 0 ? tracks[^1].ServerVersion : since
        }.Max();

        return Ok(new SyncChangesResponse
        {
            Games = games,
            Cars = cars,
            Tracks = tracks,
            NextVersion = nextVersion,
            HasMore = hasMore
        });
    }
}
