namespace GameTracker.Domain.Enums;

/// <summary>
/// The mutation kind captured in the audit trail.
/// </summary>
public enum AuditAction
{
    Create = 0,
    Update = 1,
    Delete = 2
}
