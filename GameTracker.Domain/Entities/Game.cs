namespace GameTracker.Domain.Entities;

/// <summary>
/// A supported title (e.g. RaceRoom Racing Experience). Root of the catalogue.
/// </summary>
public class Game : ISyncable
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ShortName { get; set; } = string.Empty;

    /// <summary>Server-issued monotonic sync counter. Never set by clients.</summary>
    public long ServerVersion { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ModifiedAtUtc { get; set; }

    public ICollection<Car> Cars { get; set; } = [];

    public ICollection<Track> Tracks { get; set; } = [];
}
