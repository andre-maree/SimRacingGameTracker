using GameTracker.Domain.Enums;

namespace GameTracker.Domain.Entities;

/// <summary>
/// Single audit table for every entity. Old/new state is stored as JSON to avoid a
/// per-entity audit schema. See CHOICES AND REASONS.md.
/// </summary>
public class AuditTrail
{
    public long Id { get; set; }

    public string? UserId { get; set; }

    public string TableName { get; set; } = string.Empty;

    public AuditAction Action { get; set; }

    public string PrimaryKey { get; set; } = string.Empty;

    public string? OldValues { get; set; }

    public string? NewValues { get; set; }

    public DateTime ChangedAtUtc { get; set; }
}
