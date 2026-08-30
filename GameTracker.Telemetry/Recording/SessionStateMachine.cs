using GameTracker.Domain.Enums;
using GameTracker.Telemetry.Abstractions;

namespace GameTracker.Telemetry.Recording;

/// <summary>
/// Turns the raw telemetry frame stream into discrete recording events.
/// </summary>
/// <remarks>
/// Deliberately a pure state machine: it holds no database, no clock of its own beyond the
/// frame timestamps, and no I/O. Every decision is a function of the frames it has been
/// given, which is what makes the awkward cases (restarts, quits mid-lap, pit cycles)
/// testable without RaceRoom running at all.
/// <para>
/// The hard problem is that RaceRoom does not announce state changes. There is no "session
/// started" flag: a restart looks almost exactly like an ordinary frame, so the transitions
/// below are inferred from the few signals that are actually reliable.
/// </para>
/// </remarks>
public sealed class SessionStateMachine
{
    /// <summary>
    /// A drop in simulation time larger than this counts as a restart rather than jitter.
    /// The clock is monotonic within a session, so any meaningful decrease means the
    /// session was reset. A small tolerance absorbs the occasional stale frame.
    /// </summary>
    private const double RestartToleranceSeconds = 0.5;

    /// <summary>
    /// R3E <c>SessionPhase.Checkered</c>. The flag has fallen and the session is over, even
    /// though the car may still be rolling down the slow-down lap.
    /// </summary>
    private const int CheckeredPhase = 6;

    private Guid? _sessionId;
    private Guid? _stintId;
    private SessionType _sessionType;
    private int _carExternalId;
    private int _trackExternalId;
    private int _stintNumber;

    private double? _lastSimulationTime;
    private int _lastCompletedLaps;

    /// <summary>
    /// Latched lap validity. RaceRoom reports validity for the lap *in progress*, and a
    /// cut is often cleared before the lap ends. Latching means a single invalid frame
    /// condemns the whole lap, which is the behaviour a driver expects.
    /// </summary>
    private bool _currentLapValid = true;

    /// <summary>
    /// True when any part of the current lap was driven in the pit lane, so an in-lap or
    /// out-lap is never presented as a representative flying lap.
    /// </summary>
    private bool _currentLapTouchedPits;

    private bool _inPitLane;

    /// <summary>
    /// Latched once the session reaches the checkered phase, so the eventual return to the
    /// menus is recorded as <see cref="SessionEndReason.Completed"/> rather than
    /// <see cref="SessionEndReason.Abandoned"/>.
    /// </summary>
    /// <remarks>
    /// Latched rather than read at close time because the results screen tears the phase
    /// down: by the frame the menu flag appears, the game may no longer report Checkered.
    /// </remarks>
    private bool _sawCheckeredFlag;

    /// <summary>True while a session is open and frames are being recorded.</summary>
    public bool IsRecording => _sessionId is not null;

    /// <summary>The open session, or null when idle. Used to tag persisted frames.</summary>
    public Guid? CurrentSessionId => _sessionId;

    /// <summary>The last completed-lap count observed from the game, for diagnostics.</summary>
    public int LastCompletedLaps => _lastCompletedLaps;

    /// <summary>The open stint, or null when idle.</summary>
    public Guid? CurrentStintId => _stintId;

    /// <summary>
    /// Feeds one frame through the machine and returns any events it produced.
    /// </summary>
    /// <remarks>
    /// Returns a list because a single frame can legitimately produce several events: a
    /// restart, for example, closes a lap, a stint and a session, then opens new ones.
    /// </remarks>
    public IReadOnlyList<RecordingEvent> Process(TelemetryFrame frame)
    {
        var events = new List<RecordingEvent>();

        // Menus are the normal way a session ends: the player quits to the garage. Any lap
        // in progress is genuinely incomplete and must be discarded, not saved short.
        //
        // Pausing and watching a replay both raise the same menu flag without the session
        // having ended, so they are excluded: treating them as a quit would close a session
        // the driver is still in the middle of.
        if (frame.GameInMenus && !frame.GamePaused && !frame.GameInReplay)
        {
            // A session that reached the flag before the player returned to the menus was
            // finished, not abandoned. Without this distinction every completed race is
            // recorded as abandoned, because leaving via the results screen looks identical
            // to quitting mid-lap.
            CloseSession(
                events,
                frame,
                _sawCheckeredFlag ? SessionEndReason.Completed : SessionEndReason.Abandoned);

            return events;
        }

        // Without both ids the frame cannot be attributed to a car and track, so there is
        // nothing meaningful to record against.
        if (frame.CarExternalId is not { } carId || frame.TrackExternalId is not { } trackId)
        {
            return events;
        }

        if (_sessionId is null)
        {
            StartSession(events, frame, carId, trackId);
            return events;
        }

        // A restart is the case that silently corrupts data if missed: laps from the new
        // run would be appended to the old session. Simulation time going backwards is the
        // only dependable signal, since the car and track do not change on a restart.
        //
        // Both readings must be available. An unavailable clock reads as the -1 sentinel,
        // and comparing that against a running session's elapsed time looks like a huge
        // decrease - a phantom restart that discards the lap in progress.
        if (frame.GameSimulationTime is { } simulationTime &&
            _lastSimulationTime is { } lastSimulationTime &&
            simulationTime < lastSimulationTime - RestartToleranceSeconds)
        {
            CloseSession(events, frame, SessionEndReason.Restart);
            StartSession(events, frame, carId, trackId);
            return events;
        }

        // Changing car or track without a menu frame in between (rare, but possible when
        // frames are dropped) is still a different session.
        if (carId != _carExternalId || trackId != _trackExternalId)
        {
            CloseSession(events, frame, SessionEndReason.Completed);
            StartSession(events, frame, carId, trackId);
            return events;
        }

        _lastSimulationTime = frame.GameSimulationTime;

        if (frame.SessionPhase == CheckeredPhase)
        {
            _sawCheckeredFlag = true;
        }

        ProcessPitTransitions(events, frame);
        ProcessLapCompletion(events, frame);

        return events;
    }

    /// <summary>
    /// Signals that the telemetry source disconnected, closing anything still open.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Process"/> because a disconnect is the absence of frames:
    /// there is no frame to infer it from, so the caller must tell us.
    /// </remarks>
    public IReadOnlyList<RecordingEvent> Disconnect(DateTime occurredAtUtc)
    {
        var events = new List<RecordingEvent>();

        if (_sessionId is null)
        {
            return events;
        }

        CloseSession(events, occurredAtUtc, _lastCompletedLaps, SessionEndReason.GameClosed);
        return events;
    }

    private void StartSession(List<RecordingEvent> events, TelemetryFrame frame, int carId, int trackId)
    {
        // Ids are generated client-side so that laps can be recorded, related and displayed
        // entirely offline; the server accepts them as-is for idempotent upload.
        _sessionId = Guid.NewGuid();
        _sessionType = frame.SessionType;
        _carExternalId = carId;
        _trackExternalId = trackId;
        _lastSimulationTime = frame.GameSimulationTime;

        // Anchor on the game's own lap count rather than assuming zero: joining a session
        // in progress (or a mid-session app start) must not replay laps already driven.
        _lastCompletedLaps = frame.CompletedLaps ?? 0;

        _sawCheckeredFlag = frame.SessionPhase == CheckeredPhase;

        _stintNumber = 0;
        _inPitLane = frame.InPitLane;

        events.Add(new SessionStarted(
            frame.CapturedAtUtc,
            _sessionId.Value,
            _sessionType,
            carId,
            trackId));

        StartStint(events, frame.CapturedAtUtc, outLap: frame.InPitLane);
    }

    private void StartStint(List<RecordingEvent> events, DateTime occurredAtUtc, bool outLap)
    {
        _stintNumber++;
        _stintId = Guid.NewGuid();

        // A new stint always starts a fresh lap: validity and pit-contamination reset here,
        // not at the lap boundary, so an out-lap is correctly flagged from its first frame.
        _currentLapValid = true;
        _currentLapTouchedPits = outLap;

        events.Add(new StintStarted(
            occurredAtUtc,
            _sessionId!.Value,
            _stintId.Value,
            _stintNumber,
            outLap));
    }

    private void ProcessPitTransitions(List<RecordingEvent> events, TelemetryFrame frame)
    {
        if (frame.InPitLane == _inPitLane)
        {
            // Still mark the lap as pit-contaminated while in the lane, so a lap that both
            // starts and ends inside the pits is not treated as a flying lap.
            if (_inPitLane)
            {
                _currentLapTouchedPits = true;
            }

            return;
        }

        _inPitLane = frame.InPitLane;

        if (_inPitLane)
        {
            // Pit entry ends the stint. The in-lap itself is still completed and recorded
            // below when the line is crossed.
            _currentLapTouchedPits = true;
            EndStint(events, frame.CapturedAtUtc);
        }
        else
        {
            // Leaving the pits begins the next stint on an out-lap.
            StartStint(events, frame.CapturedAtUtc, outLap: true);
        }
    }

    private void ProcessLapCompletion(List<RecordingEvent> events, TelemetryFrame frame)
    {
        // Validity is latched: once the game says the lap is invalid, it stays invalid even
        // if the flag clears before the line.
        if (frame.CurrentLapValid is false)
        {
            _currentLapValid = false;
        }

        if (frame.CompletedLaps is not { } completedLaps || completedLaps <= _lastCompletedLaps)
        {
            return;
        }

        // The counter can advance by more than one if frames were dropped; the intermediate
        // laps have no data, so only the lap actually observed is emitted.
        var lapNumber = completedLaps;
        _lastCompletedLaps = completedLaps;

        // A stint can be closed (pit entry) while the in-lap is still running; reopen so
        // the completed lap always has an owner.
        if (_stintId is null)
        {
            StartStint(events, frame.CapturedAtUtc, outLap: true);
        }

        events.Add(new LapCompleted(
            frame.CapturedAtUtc,
            _stintId!.Value,
            Guid.NewGuid(),
            lapNumber,

            // Previous-lap values, not the last sampled current-lap values: at 60 Hz the
            // current time is always read shortly before the line and would under-report.
            frame.LapTimePrevious,
            frame.PreviousSector1,
            frame.PreviousSector2,
            frame.PreviousSector3,
            _currentLapValid,
            _currentLapTouchedPits));

        // Reset for the lap now under way. Pit contamination carries over only if the car
        // is currently in the lane.
        _currentLapValid = true;
        _currentLapTouchedPits = _inPitLane;
    }

    private void EndStint(List<RecordingEvent> events, DateTime occurredAtUtc)
    {
        if (_stintId is null)
        {
            return;
        }

        events.Add(new StintEnded(occurredAtUtc, _stintId.Value));
        _stintId = null;
    }

    private void CloseSession(List<RecordingEvent> events, TelemetryFrame frame, SessionEndReason reason)
        => CloseSession(events, frame.CapturedAtUtc, frame.CompletedLaps ?? _lastCompletedLaps, reason);

    private void CloseSession(
        List<RecordingEvent> events,
        DateTime occurredAtUtc,
        int completedLaps,
        SessionEndReason reason)
    {
        if (_sessionId is null)
        {
            return;
        }

        // A lap was under way when the session ended. It is reported as discarded rather
        // than saved with a guessed time: a truncated lap looks like a real one in a
        // results table, which is worse than an explicit gap.
        if (_stintId is not null && HasLapInProgress(completedLaps))
        {
            events.Add(new PartialLapDiscarded(occurredAtUtc, _stintId.Value, completedLaps + 1));
        }

        EndStint(events, occurredAtUtc);

        events.Add(new SessionEnded(occurredAtUtc, _sessionId.Value, reason));

        _sessionId = null;
        _stintId = null;
        _stintNumber = 0;
        _lastSimulationTime = null;
        _lastCompletedLaps = 0;
        _currentLapValid = true;
        _currentLapTouchedPits = false;
        _inPitLane = false;
        _sawCheckeredFlag = false;
    }

    /// <summary>
    /// A lap is in progress unless the session ended exactly on a lap boundary, which the
    /// completed-lap counter having already been consumed tells us.
    /// </summary>
    private bool HasLapInProgress(int completedLaps) => completedLaps >= _lastCompletedLaps;
}
