namespace GameTracker.Domain.Entities;

/// <summary>
/// Client-side sync cursor. <see cref="LastSyncedVersion"/> only advances after the
/// batch transaction commits, so an interrupted sync resumes rather than losing rows.
/// </summary>
public class SyncMetadata
{
    public int Id { get; set; }

    /// <summary>Logical name of the synced collection, e.g. "Cars".</summary>
    public string EntityName { get; set; } = string.Empty;

    public long LastSyncedVersion { get; set; }

    public DateTime? LastSyncedAtUtc { get; set; }
}
