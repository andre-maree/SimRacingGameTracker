namespace GameTracker.Domain.Enums;

/// <summary>
/// Session classification, mirroring the R3E shared-memory session values.
/// </summary>
public enum SessionType
{
    Unknown = -1,
    Practice = 0,
    Qualify = 1,
    Race = 2,
    Warmup = 3
}
