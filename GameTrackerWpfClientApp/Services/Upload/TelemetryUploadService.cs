using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using GameTracker.Domain.Dtos;
using GameTrackerWpfClientApp.Data;
using GameTrackerWpfClientApp.Services.Authentication;
using GameTrackerWpfClientApp.Services.Connectivity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameTrackerWpfClientApp.Services.Upload;

/// <summary>
/// Drains locally recorded sessions and laps to the server in the background.
/// </summary>
/// <remarks>
/// The client is offline-first, so upload is a background reconciliation rather than part
/// of the recording path: a lap is durable in SQLite the moment it is driven, and losing
/// connectivity delays publication but never data.
/// <para>
/// Both endpoints are idempotent on the client-generated GUID, which is what makes retries
/// safe. That matters because the ambiguous failure — a request that reached the server
/// and committed, but whose response was lost — is indistinguishable from a real failure
/// on the client. Retrying is therefore always the correct choice, and a duplicate is
/// reported rather than inserted.
/// </para>
/// </remarks>
public sealed class TelemetryUploadService : BackgroundService
{
    /// <summary>Matches the server's declared batch limit.</summary>
    private const int BatchSize = 500;

    /// <summary>Idle poll interval when there is nothing to send.</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

    /// <summary>First backoff step after a failure.</summary>
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Backoff ceiling. Capped because the server coming back online must be noticed
    /// within a sane window; uncapped doubling would leave a client sulking for hours
    /// after a brief restart.
    /// </summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuthenticationState _authenticationState;
    private readonly ConnectivityState _connectivityState;
    private readonly ILogger<TelemetryUploadService> _logger;

    private TimeSpan _backoff = InitialBackoff;

    /// <summary>
    /// Whether the previous cycle was skipped for want of a sign-in, so that the pause is
    /// reported once rather than on every poll.
    /// </summary>
    private bool _pausedForSignIn;

    /// <summary>
    /// The same idea for an unreachable server, tracked separately so that recovering from
    /// one does not silence the message about the other.
    /// </summary>
    private bool _pausedForOffline;

    public TelemetryUploadService(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        AuthenticationState authenticationState,
        ConnectivityState connectivityState,
        ILogger<TelemetryUploadService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _authenticationState = authenticationState;
        _connectivityState = connectivityState;
        _logger = logger;
    }

    /// <summary>Laps still queued at the last check, for status display.</summary>
    public int PendingCount { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = IdleInterval;

            try
            {
                // Nothing to attempt while signed out: firing requests that are certain to
                // 401 would only burn the backoff window and spam the log.
                if (_authenticationState.IsAuthenticated)
                {
                    if (_pausedForSignIn)
                    {
                        _pausedForSignIn = false;
                        _logger.LogInformation("Sign-in restored; resuming telemetry upload.");
                    }

                    var succeeded = await DrainAsync(stoppingToken);

                    if (succeeded)
                    {
                        _backoff = InitialBackoff;
                    }
                    else if (!_connectivityState.IsOnline)
                    {
                        // An unreachable server is not a fault to back away from
                        // exponentially: the handler already suppresses attempts during its
                        // cooldown, so polling at that same rhythm costs nothing and keeps
                        // recovery prompt once the server returns.
                        delay = ConnectivityState.Cooldown;
                        _backoff = InitialBackoff;
                    }
                    else
                    {
                        delay = _backoff;
                        _backoff = Min(_backoff * 2, MaxBackoff);
                    }
                }
                else if (!_pausedForSignIn)
                {
                    // Reported once on the transition. A token that lapses while the app is
                    // running otherwise strands the queue in perfect silence: laps keep
                    // recording, the grid keeps saying "queued", and nothing anywhere says
                    // that uploading has stopped or why.
                    _pausedForSignIn = true;
                    PendingCount = await CountPendingAsync(stoppingToken);

                    _logger.LogWarning(
                        "Telemetry upload is paused because the session is not signed in; " +
                        "{PendingCount} lap(s) are queued and will be sent after the next sign-in.",
                        PendingCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The uploader must outlive any single failure: it is the only thing that
                // ever clears the local queue, so letting it die would silently strand
                // every lap recorded from that point on.
                _logger.LogError(ex, "Upload cycle failed; backing off.");
                delay = _backoff;
                _backoff = Min(_backoff * 2, MaxBackoff);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Sends every queued lap. Returns false when the cycle should back off.
    /// </summary>
    private async Task<bool> DrainAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(ApiClientNames.GameTrackerApi);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ClientDbContext>();

            // A correlation id per batch: an upload that partially succeeds spans several
            // log lines, and without a shared key they cannot be tied together after the fact.
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["UploadBatchId"] = Guid.NewGuid()
            });

            // Oldest first, so a long backlog is published in the order it was driven.
            var pending = await context.LocalTelemetry
                .Where(r => r.UploadedAtUtc == null)
                .OrderBy(r => r.RecordedAtUtc)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            PendingCount = pending.Count;

            if (pending.Count == 0)
            {
                return true;
            }

            // Sessions are upserted before their laps: the server stores laps against a
            // session id, so publishing laps first would briefly expose rows whose parent
            // session does not exist yet.
            var sessionIds = pending.Select(r => r.SessionId).Distinct().ToList();

            foreach (var sessionId in sessionIds)
            {
                if (!await UploadSessionAsync(client, context, sessionId, cancellationToken))
                {
                    return false;
                }
            }

            var request = new TelemetryBatchRequest
            {
                Records = pending.Select(r => new TelemetryRecordDto
                {
                    Id = r.Id,
                    SessionId = r.SessionId,
                    GameId = r.GameId,
                    CarExternalId = r.CarExternalId,
                    TrackExternalId = r.TrackExternalId,
                    LapNumber = r.LapNumber,
                    LapTime = r.LapTime,
                    IsValid = r.IsValid,
                    RecordedAtUtc = r.RecordedAtUtc
                }).ToList()
            };

            var response = await client.PostAsJsonAsync("api/telemetry/batch", request, cancellationToken);

            if (!await IsUsableResponseAsync(response, "telemetry batch", cancellationToken))
            {
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<TelemetryBatchResponse>(cancellationToken);

            // Stamped only after a 2xx, and duplicates count as delivered: the server
            // reporting a record as already present is positive confirmation that an
            // earlier attempt committed, so leaving it queued would retry it forever.
            var uploadedAtUtc = DateTime.UtcNow;

            foreach (var record in pending)
            {
                record.UploadedAtUtc = uploadedAtUtc;
            }

            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Uploaded {Count} laps ({Accepted} accepted, {Duplicates} already present).",
                pending.Count,
                result?.Accepted ?? 0,
                result?.Duplicates ?? 0);

            PendingCount = 0;

            // A short batch means the queue is drained; anything else loops for the next page.
            if (pending.Count < BatchSize)
            {
                return true;
            }
        }

        return true;
    }

    private async Task<bool> UploadSessionAsync(
        HttpClient client,
        ClientDbContext context,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await context.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            // A lap with no local session row cannot be attributed; skipping it here is
            // better than blocking the whole queue behind it.
            _logger.LogWarning("Queued laps reference unknown session {SessionId}.", sessionId);
            return true;
        }

        var dto = new SessionUploadDto
        {
            Id = session.Id,
            GameId = session.GameId,
            CarExternalId = session.CarExternalId,
            TrackExternalId = session.TrackExternalId,
            SessionType = session.SessionType,
            StartedAtUtc = session.StartedAtUtc,
            EndedAtUtc = session.EndedAtUtc,
            EndReason = session.EndReason
        };

        // Re-posted on every drain rather than tracked as "sent once": the endpoint is an
        // upsert, and a session uploaded mid-run must later have its closing details
        // published once the driver leaves the track.
        var response = await client.PostAsJsonAsync("api/sessions", dto, cancellationToken);

        return await IsUsableResponseAsync(response, "session upload", cancellationToken);
    }

    /// <summary>
    /// Logs and classifies a response, returning true only for success.
    /// </summary>
    private async Task<bool> IsUsableResponseAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            _pausedForOffline = false;
            return true;
        }

        if (AuthenticationHandler.IsOffline(response))
        {
            // Not a rejection: nothing reached the server, so there is no body to read and
            // nothing about the payload to correct. Reported once per offline spell.
            if (!_pausedForOffline)
            {
                _pausedForOffline = true;
                _logger.LogInformation(
                    "Upload paused during {Operation}: the server is unreachable. Laps stay queued locally.",
                    operation);
            }

            return false;
        }

        _pausedForOffline = false;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The authentication handler has already cleared the token; retrying the same
            // request would only produce the same 401.
            _logger.LogInformation("Upload paused: sign-in is required.");
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // 4xx other than 401 is logged at Warning because it will never succeed on retry:
        // the payload itself is wrong, and it needs a human, not another attempt.
        var level = (int)response.StatusCode is >= 400 and < 500
            ? LogLevel.Warning
            : LogLevel.Information;

        _logger.Log(level, "Server rejected {Operation} with {Status}: {Body}", operation, response.StatusCode, body);

        return false;
    }

    /// <summary>
    /// Counts the queued laps without attempting to send them, for status reporting while
    /// upload is paused.
    /// </summary>
    private async Task<int> CountPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ClientDbContext>();

            return await context.LocalTelemetry
                .CountAsync(r => r.UploadedAtUtc == null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Status reporting must never be the thing that breaks the uploader.
            _logger.LogDebug(ex, "Could not count queued laps while upload is paused.");
            return PendingCount;
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
