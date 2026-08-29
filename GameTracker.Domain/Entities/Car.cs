namespace GameTracker.Domain.Entities;

/// <summary>
/// A car within a game. <see cref="ExternalId"/> is the game's own id (R3E ModelId)
/// and is unique per game, not globally.
/// </summary>
public class Car : ISyncable
{
    public int Id { get; set; }

    public int GameId { get; set; }

    public Game? Game { get; set; }

    /// <summary>The game's native identifier (R3E <c>ModelId</c>).</summary>
    public int ExternalId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Manufacturer { get; set; }

    public string? Class { get; set; }

    public int? Year { get; set; }

    public long ServerVersion { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ModifiedAtUtc { get; set; }
}
