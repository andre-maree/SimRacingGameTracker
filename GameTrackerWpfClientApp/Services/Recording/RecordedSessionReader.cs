using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameTracker.Domain.Enums;
using GameTracker.Telemetry.Recording;
using GameTrackerWpfClientApp.Data;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerWpfClientApp.Services.Recording;

/// <summary>A recorded session as shown in the sessions grid.</summary>
/// <remarks>
/// The upload counts are kept as two numbers rather than one "is uploaded" flag: a session
/// with nothing queued has not necessarily been uploaded, it may simply have nothing to
/// send, and collapsing the two states let an entirely unsent session claim otherwise.
/// </remarks>
public sealed record RecordedSessionRow(
    Guid Id,
    SessionType SessionType,
    string CarName,
    string TrackName,
    DateTime StartedAtLocal,
    DateTime? EndedAtLocal,
    SessionEndReason? EndReason,
    int LapCount,
    int ValidLapCount,
    double? BestLapTime,
    int PendingUploadCount,
    int UploadedCount);

/// <summary>A lap as shown in the detail grid, already flattened across stints.</summary>
public sealed record RecordedLapRow(
    Guid Id,
    int StintNumber,
    int LapNumber,
    double? LapTime,
    double? Sector1,
    double? Sector2,
    double? Sector3,
    bool IsValid,
    bool IsPitLap,
    bool IsUploaded,
    bool HasInputTrace);

/// <summary>
/// Read-only projections of locally recorded sessions for the desktop UI.
/// </summary>
/// <remarks>
/// Projects to flat rows rather than handing entities to the grid: the UI needs car and
/// track *names* from the catalogue mirror, which are joined on external id and are not
/// navigation properties. Doing the join here keeps the components free of query logic and
/// avoids a per-row lookup while rendering.
/// </remarks>
public sealed class RecordedSessionReader
{
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;

    public RecordedSessionReader(IDbContextFactory<ClientDbContext> contextFactory) =>
        _contextFactory = contextFactory;

    /// <summary>Lists recorded sessions, most recent first.</summary>
    public async Task<IReadOnlyList<RecordedSessionRow>> GetSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        // A short-lived context per query: the page is long-lived, and a shared context
        // would keep every browsed session tracked for the lifetime of the window.
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var sessions = await context.Sessions
            .AsNoTracking()
            .OrderByDescending(s => s.StartedAtUtc)
            .Select(s => new
            {
                s.Id,
                s.GameId,
                s.SessionType,
                s.CarExternalId,
                s.TrackExternalId,
                s.StartedAtUtc,
                s.EndedAtUtc,
                s.EndReason,

                // Aggregated in SQL rather than by materialising every lap: a long
                // endurance session can hold hundreds of laps that the summary row would
                // otherwise pull across just to count them.
                LapCount = s.Stints.SelectMany(t => t.Laps).Count(),
                ValidLapCount = s.Stints.SelectMany(t => t.Laps).Count(l => l.IsValid),

                // Only valid laps count towards a best: an invalid lap is not a lap time.
                BestLapTime = s.Stints
                    .SelectMany(t => t.Laps)
                    .Where(l => l.IsValid && l.LapTime != null)
                    .Min(l => l.LapTime)
            })
            .ToListAsync(cancellationToken);

        // Both counts come from one grouping: a second query would be a second round trip
        // for a number the first one already had in hand.
        var uploadStateBySession = await context.LocalTelemetry
            .AsNoTracking()
            .GroupBy(r => r.SessionId)
            .Select(g => new
            {
                SessionId = g.Key,
                Pending = g.Count(r => r.UploadedAtUtc == null),
                Uploaded = g.Count(r => r.UploadedAtUtc != null)
            })
            .ToDictionaryAsync(g => g.SessionId, g => (g.Pending, g.Uploaded), cancellationToken);

        var cars = await context.Cars
            .AsNoTracking()
            .Select(c => new { c.GameId, c.ExternalId, c.Name })
            .ToListAsync(cancellationToken);

        var tracks = await context.Tracks
            .AsNoTracking()
            .Select(t => new { t.GameId, t.ExternalId, t.Name })
            .ToListAsync(cancellationToken);

        var carLookup = cars
            .GroupBy(c => (c.GameId, c.ExternalId))
            .ToDictionary(g => g.Key, g => g.First().Name);

        var trackLookup = tracks
            .GroupBy(t => (t.GameId, t.ExternalId))
            .ToDictionary(g => g.Key, g => g.First().Name);

        return sessions.Select(s =>
        {
            var (pending, uploaded) = uploadStateBySession.TryGetValue(s.Id, out var counts)
                ? counts
                : (0, 0);

            return new RecordedSessionRow(
                s.Id,
                s.SessionType,

                // Falls back to the raw id when the catalogue has not synced yet. Showing the
                // id is more useful than an empty cell, and the session is still valid data.
                carLookup.TryGetValue((s.GameId, s.CarExternalId), out var carName)
                    ? carName
                    : $"Car #{s.CarExternalId}",
                trackLookup.TryGetValue((s.GameId, s.TrackExternalId), out var trackName)
                    ? trackName
                    : $"Track #{s.TrackExternalId}",

                // Converted to local time for display: the driver thinks in the clock on the
                // wall, while everything is stored in UTC.
                s.StartedAtUtc.ToLocalTime(),
                s.EndedAtUtc?.ToLocalTime(),
                s.EndReason,
                s.LapCount,
                s.ValidLapCount,
                s.BestLapTime,
                pending,
                uploaded);
        })
            .ToList();
    }

    /// <summary>Lists the laps of one session, flattened across its stints.</summary>
    public async Task<IReadOnlyList<RecordedLapRow>> GetLapsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var laps = await context.Laps
            .AsNoTracking()
            .Where(l => l.Stint!.SessionId == sessionId)
            .OrderBy(l => l.Stint!.StintNumber)
            .ThenBy(l => l.LapNumber)
            .Select(l => new
            {
                l.Id,
                StintNumber = l.Stint!.StintNumber,
                l.LapNumber,
                l.LapTime,
                l.Sector1,
                l.Sector2,
                l.Sector3,
                l.IsValid,
                l.IsPitLap,

                // Existence only: the compressed blob is deliberately not loaded here, so
                // opening a session does not pull megabytes of input traces.
                HasInputTrace = l.InputTelemetry != null
            })
            .ToListAsync(cancellationToken);

        var lapIds = laps.Select(l => l.Id).ToList();

        var uploaded = await context.LocalTelemetry
            .AsNoTracking()
            .Where(r => lapIds.Contains(r.Id) && r.UploadedAtUtc != null)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        var uploadedSet = uploaded.ToHashSet();

        return laps.Select(l => new RecordedLapRow(
            l.Id,
            l.StintNumber,
            l.LapNumber,
            l.LapTime,
            l.Sector1,
            l.Sector2,
            l.Sector3,
            l.IsValid,
            l.IsPitLap,
            uploadedSet.Contains(l.Id),
            l.HasInputTrace))
            .ToList();
    }

    /// <summary>
    /// Loads a lap's input trace, preferring the cheap preview.
    /// </summary>
    /// <remarks>
    /// <paramref name="fullResolution"/> is opt-in so the default render path never
    /// inflates a blob. Only a zoom or a two-lap overlay justifies decompressing ~16,000
    /// samples per channel.
    /// </remarks>
    public async Task<DecodedInputTrace?> GetInputTraceAsync(
        Guid lapId,
        bool fullResolution = false,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Projected so the unwanted blob column is not transferred: selecting the entity
        // would pull the full compressed payload even when only the preview is needed.
        var trace = await context.LapInputTelemetry
            .AsNoTracking()
            .Where(t => t.LapId == lapId)
            .Select(t => new
            {
                t.SampleCount,
                t.SampleRateHz,
                t.PreviewSampleCount,
                Preview = fullResolution ? null : t.Preview,
                Compressed = fullResolution ? t.CompressedChannels : null
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (trace is null)
        {
            return null;
        }

        if (fullResolution && trace.Compressed is not null)
        {
            var full = LapInputTraceDecoder.DecodeFull(trace.Compressed, trace.SampleCount, trace.SampleRateHz);

            if (full is not null)
            {
                return full;
            }

            // A corrupt or truncated blob falls back to the preview rather than showing
            // nothing: a low-resolution trace is still useful, and the preview is stored
            // independently of the compressed payload.
            return await GetInputTraceAsync(lapId, fullResolution: false, cancellationToken);
        }

        return trace.Preview is null
            ? null
            : LapInputTraceDecoder.DecodePreview(trace.Preview, trace.PreviewSampleCount, trace.SampleRateHz);
    }

    /// <summary>
    /// Formats a lap or sector time for display.
    /// </summary>
    /// <remarks>
    /// Null renders as an em dash rather than 0:00.000, because a missing time and a zero
    /// time mean very different things and a zero would look like a real (absurd) result.
    /// </remarks>
    public static string FormatTime(double? seconds)
    {
        if (seconds is not { } value || value <= 0)
        {
            return "—";
        }

        var span = TimeSpan.FromSeconds(value);

        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss\.fff")
            : span.ToString(@"m\:ss\.fff");
    }
}
