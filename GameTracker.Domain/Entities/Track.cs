namespace GameTracker.Domain.Entities;

/// <summary>
/// A track layout within a game. <see cref="ExternalId"/> is the R3E <c>LayoutId</c>,
/// which is what shared memory reports, so it is the value telemetry joins on.
/// </summary>
public class Track : ISyncable
{
    public int Id { get; set; }

    public int GameId { get; set; }

    public Game? Game { get; set; }

    /// <summary>The game's native layout identifier (R3E <c>LayoutId</c>).</summary>
    public int ExternalId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? LayoutName { get; set; }

    public string? Country { get; set; }

    public double? LengthMetres { get; set; }

    public long ServerVersion { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ModifiedAtUtc { get; set; }
}
