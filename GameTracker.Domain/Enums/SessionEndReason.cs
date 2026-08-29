namespace GameTracker.Domain.Enums;

/// <summary>
/// Why a recorded session stopped. Drives the four scenarios called out in the brief.
/// </summary>
public enum SessionEndReason
{
    Completed = 0,
    Restart = 1,
    Abandoned = 2,
    GameClosed = 3
}
