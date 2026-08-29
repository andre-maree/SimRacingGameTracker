namespace GameTracker.Domain.Entities;

/// <summary>
/// A run between pit exits. A pit stop closes the current stint and opens the next
/// one with <see cref="OutLap"/> set.
/// </summary>
public class Stint
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public Session? Session { get; set; }

    public int StintNumber { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    /// <summary>True when the stint begins with an out-lap from the pit lane.</summary>
    public bool OutLap { get; set; }

    public ICollection<Lap> Laps { get; set; } = [];
}
