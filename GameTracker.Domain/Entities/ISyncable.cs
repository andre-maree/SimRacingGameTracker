namespace GameTracker.Domain.Entities;

/// <summary>
/// Marks a catalogue entity that participates in incremental sync.
/// </summary>
/// <remarks>
/// <see cref="ServerVersion"/> is allocated server-side from a monotonic SQL sequence on
/// every insert, update and soft-delete. Clients never write it: their clocks and their
/// ordering cannot be trusted. <see cref="IsDeleted"/> makes deletes visible to clients
/// as tombstones instead of rows that simply vanish.
/// </remarks>
public interface ISyncable
{
    long ServerVersion { get; set; }

    bool IsDeleted { get; set; }

    DateTime CreatedAtUtc { get; set; }

    DateTime ModifiedAtUtc { get; set; }
}
