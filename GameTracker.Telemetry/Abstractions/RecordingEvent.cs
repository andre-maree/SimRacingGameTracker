using GameTracker.Domain.Enums;

namespace GameTracker.Telemetry.Abstractions;

/// <summary>
/// An event emitted by the session state machine. The state machine is a pure function
/// of the frame stream, so these events are the only thing the persistence layer acts on.
/// </summary>
public abstract record RecordingEvent(DateTime OccurredAtUtc);

/// <summary>A new session was detected (fresh entry or a restart of the previous one).</summary>
public sealed record SessionStarted(
    DateTime OccurredAtUtc,
    Guid SessionId,
    SessionType SessionType,
    int CarExternalId,
    int TrackExternalId) : RecordingEvent(OccurredAtUtc);

/// <summary>The session finished, was restarted, or was abandoned mid-lap.</summary>
public sealed record SessionEnded(
    DateTime OccurredAtUtc,
    Guid SessionId,
    SessionEndReason Reason) : RecordingEvent(OccurredAtUtc);

/// <summary>A new stint began. <paramref name="OutLap"/> is set when leaving the pit lane.</summary>
public sealed record StintStarted(
    DateTime OccurredAtUtc,
    Guid SessionId,
    Guid StintId,
    int StintNumber,
    bool OutLap) : RecordingEvent(OccurredAtUtc);

/// <summary>The current stint ended, typically on pit entry or session end.</summary>
public sealed record StintEnded(
    DateTime OccurredAtUtc,
    Guid StintId) : RecordingEvent(OccurredAtUtc);

/// <summary>
/// A lap crossed the line. Invalid laps are still emitted, with <paramref name="LapTime"/>
/// null when the game never reported a time, so lap counts always reconcile.
/// </summary>
public sealed record LapCompleted(
    DateTime OccurredAtUtc,
    Guid StintId,
    Guid LapId,
    int LapNumber,
    double? LapTime,
    double? Sector1,
    double? Sector2,
    double? Sector3,
    bool IsValid,
    bool IsPitLap) : RecordingEvent(OccurredAtUtc);

/// <summary>A partial lap was discarded, e.g. the player quit to the menus mid-lap.</summary>
public sealed record PartialLapDiscarded(
    DateTime OccurredAtUtc,
    Guid StintId,
    int LapNumber) : RecordingEvent(OccurredAtUtc);
