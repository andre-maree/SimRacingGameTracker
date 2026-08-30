using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GameTracker.Domain.Entities;
using GameTracker.Domain.Enums;
using GameTracker.Telemetry.Abstractions;
using GameTracker.Telemetry.Recording;
using GameTrackerWpfClientApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameTrackerWpfClientApp.Services.Recording;

/// <summary>A snapshot of the recorder for display in the UI.</summary>
public sealed record RecordingStatus(
    bool IsConnected,
    bool IsRecording,
    Guid? SessionId,
    int LapsRecorded,
    string? LastLapDescription);

/// <summary>
/// The recording pipeline: reads telemetry frames, feeds them through
/// <see cref="SessionStateMachine"/>, and persists the resulting events to local SQLite.
/// </summary>
/// <remarks>
/// Producer and consumer are deliberately split by a bounded channel. The poll loop must
/// keep to its 60 Hz cadence, and SQLite writes and Brotli compression are both far too
/// slow and too jittery to sit on it: a single blocking write would skip the frame a lap
/// rolls over on, which is precisely the frame that matters.
/// <para>
/// The channel is bounded rather than unbounded because an unbounded queue turns a stalled
/// disk into unbounded memory growth over a long stint. With a bound, the failure is
/// visible and contained.
/// </para>
/// </remarks>
public sealed class SessionRecorder : BackgroundService
{
    /// <summary>
    /// About ten seconds of 60 Hz frames. Large enough to absorb a slow write or a
    /// compression pass, small enough that a genuine stall is caught rather than hidden.
    /// </summary>
    private const int ChannelCapacity = 600;

    /// <summary>Poll rate of the telemetry source, recorded against each lap's trace.</summary>
    private const int SampleRateHz = 60;

    /// <summary>
    /// Minimum gap between UI notifications. The frame stream would otherwise re-render
    /// the status 60 times a second to say the same thing.
    /// </summary>
    private static readonly TimeSpan NotificationInterval = TimeSpan.FromMilliseconds(500);

    private readonly ITelemetrySource _telemetrySource;
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;
    private readonly ILogger<SessionRecorder> _logger;

    private readonly Channel<TelemetryFrame> _frames = Channel.CreateBounded<TelemetryFrame>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            // Drop the oldest frame under pressure rather than blocking the producer.
            // Losing a stale sample costs a gap in an input trace; stalling the poll loop
            // costs a missed lap boundary, which is unrecoverable.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

    private readonly SessionStateMachine _stateMachine = new();
    private readonly LapInputTraceBuffer _inputTrace = new(SampleRateHz);

    private int _gameId;
    private int _carExternalId;
    private int _trackExternalId;
    private int _lapsRecorded;
    private string? _lastLapDescription;
    private DateTime _lastNotifiedUtc = DateTime.MinValue;

    /// <summary>Last lap count logged, so the diagnostic fires on change rather than per frame.</summary>
    private int? _lastLoggedCompletedLaps = int.MinValue;

    public SessionRecorder(
        ITelemetrySource telemetrySource,
        IDbContextFactory<ClientDbContext> contextFactory,
        ILogger<SessionRecorder> logger)
    {
        _telemetrySource = telemetrySource;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>Raised at most twice a second while recording, for status display.</summary>
    public event Action<RecordingStatus>? StatusChanged;

    public RecordingStatus CurrentStatus => new(
        _telemetrySource.IsConnected,
        _stateMachine.IsRecording,
        _stateMachine.CurrentSessionId,
        _lapsRecorded,
        _lastLapDescription);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var producer = ProduceAsync(stoppingToken);
        var consumer = ConsumeAsync(stoppingToken);

        await Task.WhenAll(producer, consumer);
    }

    /// <summary>Pumps the telemetry source into the channel, doing no work of its own.</summary>
    private async Task ProduceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _telemetrySource.ReadFramesAsync(cancellationToken))
            {
                await _frames.Writer.WriteAsync(frame, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telemetry producer stopped unexpectedly.");
        }
        finally
        {
            // Completing the writer lets the consumer flush what is already queued and
            // close any open session, rather than losing the tail of the stint.
            _frames.Writer.TryComplete();
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                await ProcessFrameAsync(frame, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telemetry consumer stopped unexpectedly.");
        }

        // On shutdown an in-progress session is closed explicitly: the state machine
        // cannot infer the absence of frames on its own.
        await FlushOnShutdownAsync();
    }

    private async Task ProcessFrameAsync(TelemetryFrame frame, CancellationToken cancellationToken)
    {
        var anchorBefore = _stateMachine.LastCompletedLaps;
        var hadSession = _stateMachine.IsRecording;

        var events = _stateMachine.Process(frame);

        // The lap counter is the sole input to lap detection, so every change in it is
        // logged alongside whether a lap was actually emitted. A lap that the game counted
        // but the recorder did not is otherwise invisible until the results are read back.
        if (frame.CompletedLaps != _lastLoggedCompletedLaps)
        {
            _lastLoggedCompletedLaps = frame.CompletedLaps;

            _logger.LogInformation(
                "Lap counter now {Reported} (recorder anchor was {Before}, now {After}); " +
                "emitted {LapEvents} lap event(s), {Discarded} discarded. " +
                "Phase={Phase} InPitLane={InPitLane} SimTime={SimTime} Recording={Recording}.",
                frame.CompletedLaps,
                anchorBefore,
                _stateMachine.LastCompletedLaps,
                events.OfType<LapCompleted>().Count(),
                events.OfType<PartialLapDiscarded>().Count(),
                frame.SessionPhase,
                frame.InPitLane,
                frame.GameSimulationTime,
                hadSession);
        }

        // The anchor chosen when a session opens decides which laps are considered already
        // driven. If it is set above zero at the start of a race, the opening lap can never
        // be emitted, so the value and the frame it came from are recorded verbatim.
        foreach (var started in events.OfType<SessionStarted>())
        {
            _logger.LogInformation(
                "Session {SessionId} started: anchored lap counter at {Anchor} from game value " +
                "{Reported}. Phase={Phase} InPitLane={InPitLane} LapDistance={LapDistance} " +
                "SimTime={SimTime} SessionType={SessionType}.",
                started.SessionId,
                _stateMachine.LastCompletedLaps,
                frame.CompletedLaps,
                frame.SessionPhase,
                frame.InPitLane,
                frame.LapDistanceFraction,
                frame.GameSimulationTime,
                frame.SessionType);
        }

        // A session ending is inferred, never announced by the game, so the frame that
        // triggered it is logged in full. Without this the only visible symptom of a
        // misread shared-memory field is a session that mysteriously reads 'Abandoned'.
        foreach (var ended in events.OfType<SessionEnded>())
        {
            _logger.LogInformation(
                "Session {SessionId} ended: {Reason}. Frame: InMenus={InMenus} SimTime={SimTime:F3} " +
                "SessionType={SessionType} SessionPhase={SessionPhase} CarId={CarId} TrackId={TrackId} " +
                "CompletedLaps={CompletedLaps} LapDistance={LapDistance} InPitLane={InPitLane}.",
                ended.SessionId,
                ended.Reason,
                frame.GameInMenus,
                frame.GameSimulationTime,
                frame.SessionType,
                frame.SessionPhase,
                frame.CarExternalId,
                frame.TrackExternalId,
                frame.CompletedLaps,
                frame.LapDistanceFraction,
                frame.InPitLane);
        }

        // Inputs are buffered only while a stint is genuinely open, so menu and garage
        // frames never leak into a lap's trace.
        if (_stateMachine.CurrentStintId is not null && !frame.GameInMenus)
        {
            _inputTrace.Add(frame.Throttle, frame.Brake, frame.Steering);
        }

        if (events.Count > 0)
        {
            await PersistAsync(events, cancellationToken);
        }

        NotifyThrottled(frame.CapturedAtUtc);
    }

    /// <summary>
    /// Applies one frame's worth of events in a single transaction.
    /// </summary>
    /// <remarks>
    /// A short-lived context per batch rather than one long-lived context: the recorder
    /// runs for the lifetime of the app, and a persistent change tracker would accumulate
    /// every lap of every session in memory. Batching by frame keeps the transaction
    /// aligned with the state machine's own atomic unit, so a restart cannot commit the
    /// new session without also closing the old one.
    /// </remarks>
    private async Task PersistAsync(IReadOnlyList<RecordingEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            // Scoped to the session so every write, and any failure, is attributable to a
            // specific outing when read back from the flat log file.
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["SessionId"] = _stateMachine.CurrentSessionId ?? Guid.Empty
            });

            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            foreach (var recordingEvent in events)
            {
                await ApplyAsync(context, recordingEvent, cancellationToken);
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // A failed write must not kill the pipeline: the driver is still on track, and
            // abandoning the stream would lose every remaining lap as well as this one.
            _logger.LogError(ex, "Failed to persist recording events; recording continues.");
        }
    }

    private async Task ApplyAsync(
        ClientDbContext context,
        RecordingEvent recordingEvent,
        CancellationToken cancellationToken)
    {
        switch (recordingEvent)
        {
            case SessionStarted started:
                await ApplySessionStartedAsync(context, started, cancellationToken);
                break;

            case SessionEnded ended:
                await ApplySessionEndedAsync(context, ended, cancellationToken);
                break;

            case StintStarted stintStarted:
                context.Stints.Add(new Stint
                {
                    Id = stintStarted.StintId,
                    SessionId = stintStarted.SessionId,
                    StintNumber = stintStarted.StintNumber,
                    StartedAtUtc = stintStarted.OccurredAtUtc,
                    OutLap = stintStarted.OutLap
                });
                break;

            case StintEnded stintEnded:
                await ApplyStintEndedAsync(context, stintEnded, cancellationToken);
                break;

            case LapCompleted lap:
                ApplyLapCompleted(context, lap);
                break;

            case PartialLapDiscarded discarded:
                // Nothing is written: a truncated lap looks real in a results table, which
                // is worse than an explicit gap. The buffered inputs go with it.
                _inputTrace.Clear();
                _logger.LogInformation(
                    "Discarded partial lap {LapNumber} on stint {StintId}.",
                    discarded.LapNumber,
                    discarded.StintId);
                break;
        }
    }

    private async Task ApplySessionStartedAsync(
        ClientDbContext context,
        SessionStarted started,
        CancellationToken cancellationToken)
    {
        _gameId = await ResolveGameIdAsync(context, cancellationToken);
        _carExternalId = started.CarExternalId;
        _trackExternalId = started.TrackExternalId;

        // The trace buffer is reset per session as well as per lap, so a stale tail from
        // an abandoned session can never be attached to the first lap of the next one.
        _inputTrace.Clear();

        context.Sessions.Add(new Session
        {
            Id = started.SessionId,
            GameId = _gameId,
            CarExternalId = started.CarExternalId,
            TrackExternalId = started.TrackExternalId,
            SessionType = started.SessionType,
            StartedAtUtc = started.OccurredAtUtc
        });

        _logger.LogInformation(
            "Recording session {SessionId} ({SessionType}) car {CarId} track {TrackId}.",
            started.SessionId,
            started.SessionType,
            started.CarExternalId,
            started.TrackExternalId);
    }

    private async Task ApplySessionEndedAsync(
        ClientDbContext context,
        SessionEnded ended,
        CancellationToken cancellationToken)
    {
        var session = await context.Sessions
            .FirstOrDefaultAsync(s => s.Id == ended.SessionId, cancellationToken);

        if (session is null)
        {
            return;
        }

        session.EndedAtUtc = ended.OccurredAtUtc;
        session.EndReason = ended.Reason;

        _inputTrace.Clear();
    }

    private static async Task ApplyStintEndedAsync(
        ClientDbContext context,
        StintEnded stintEnded,
        CancellationToken cancellationToken)
    {
        var stint = await context.Stints
            .FirstOrDefaultAsync(s => s.Id == stintEnded.StintId, cancellationToken);

        if (stint is not null)
        {
            stint.EndedAtUtc = stintEnded.OccurredAtUtc;
        }
    }

    private void ApplyLapCompleted(ClientDbContext context, LapCompleted lap)
    {
        context.Laps.Add(new Lap
        {
            Id = lap.LapId,
            StintId = lap.StintId,
            LapNumber = lap.LapNumber,
            // Invalid laps are stored with a null time rather than dropped, so local lap
            // counts always reconcile with the game's.
            LapTime = lap.LapTime,
            Sector1 = lap.Sector1,
            Sector2 = lap.Sector2,
            Sector3 = lap.Sector3,
            IsValid = lap.IsValid,
            IsPitLap = lap.IsPitLap,
            CompletedAtUtc = lap.OccurredAtUtc
        });

        var trace = _inputTrace.Encode();

        if (trace is not null)
        {
            context.LapInputTelemetry.Add(new LapInputTelemetry
            {
                Id = Guid.NewGuid(),
                LapId = lap.LapId,
                SampleCount = trace.SampleCount,
                SampleRateHz = trace.SampleRateHz,
                CompressedChannels = trace.CompressedChannels,
                Preview = trace.Preview,
                PreviewSampleCount = trace.PreviewSampleCount
            });
        }

        _inputTrace.Clear();

        // The upload queue row is written in the same transaction as the lap. Writing it
        // separately would allow a crash between the two to leave a lap that is never
        // uploaded, with nothing to indicate it is missing.
        context.LocalTelemetry.Add(new TelemetryRecord
        {
            Id = lap.LapId,
            GameId = _gameId,
            CarExternalId = _carExternalId,
            TrackExternalId = _trackExternalId,
            SessionId = _stateMachine.CurrentSessionId ?? Guid.Empty,
            LapNumber = lap.LapNumber,
            LapTime = lap.LapTime,
            IsValid = lap.IsValid,
            RecordedAtUtc = lap.OccurredAtUtc,

            // Null marks it as queued; the uploader stamps this only after a 2xx.
            UploadedAtUtc = null
        });

        _lapsRecorded++;
        _lastLapDescription = lap.LapTime is { } time
            ? $"Lap {lap.LapNumber}: {TimeSpan.FromSeconds(time):mm\\:ss\\.fff}{(lap.IsValid ? string.Empty : " (invalid)")}"
            : $"Lap {lap.LapNumber}: —";

        // Force the next notification through: a completed lap is exactly the event the
        // user is waiting to see, so it should not be swallowed by the throttle window.
        _lastNotifiedUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Resolves the local Game row id for RaceRoom.
    /// </summary>
    /// <remarks>
    /// Looked up from the synced catalogue rather than hard-coded, because the id is
    /// server-issued. Falls back to 0 when the catalogue has not synced yet: the lap is
    /// still worth keeping, and the uploader resolves the game server-side anyway.
    /// </remarks>
    private static async Task<int> ResolveGameIdAsync(ClientDbContext context, CancellationToken cancellationToken)
    {
        return await context.Games
            .Where(g => g.ShortName == "R3E")
            .Select(g => g.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task FlushOnShutdownAsync()
    {
        var events = _stateMachine.Disconnect(DateTime.UtcNow);

        if (events.Count == 0)
        {
            return;
        }

        // CancellationToken.None on purpose: this runs during shutdown, when the stopping
        // token is already cancelled, and the point of the flush is to close the session.
        await PersistAsync(events, CancellationToken.None);
        Notify();
    }

    private void NotifyThrottled(DateTime nowUtc)
    {
        if (nowUtc - _lastNotifiedUtc < NotificationInterval)
        {
            return;
        }

        _lastNotifiedUtc = nowUtc;
        Notify();
    }

    private void Notify()
    {
        // Handler faults belong to the UI, not the recorder: a broken status binding must
        // not be able to stop laps being saved.
        try
        {
            StatusChanged?.Invoke(CurrentStatus);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording status handler threw; ignoring.");
        }
    }
}
