namespace GameTracker.Domain.Enums;

/// <summary>
/// Result of a catalogue synchronisation run.
/// </summary>
public enum SyncOutcome
{
    Success = 0,
    UpToDate = 1,
    Interrupted = 2,
    Unauthorized = 3,
    NetworkFailure = 4,
    Failed = 5
}
