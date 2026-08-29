namespace GameTracker.Domain.Dtos;

/// <summary>Catalogue row as returned by <c>GET /api/sync/changes</c>. Tombstones carry <see cref="IsDeleted"/>.</summary>
public class CarSyncDto
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public int ExternalId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? Class { get; set; }
    public int? Year { get; set; }
    public long ServerVersion { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Track catalogue row as returned by <c>GET /api/sync/changes</c>.</summary>
public class TrackSyncDto
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public int ExternalId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LayoutName { get; set; }
    public string? Country { get; set; }
    public double? LengthMetres { get; set; }
    public long ServerVersion { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Game catalogue row as returned by <c>GET /api/sync/changes</c>.</summary>
public class GameSyncDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public long ServerVersion { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// One page of catalogue changes. The client applies the page in a transaction and only
/// then stores <see cref="NextVersion"/>, so an interrupted sync resumes cleanly.
/// </summary>
public class SyncChangesResponse
{
    public IReadOnlyList<GameSyncDto> Games { get; set; } = [];
    public IReadOnlyList<CarSyncDto> Cars { get; set; } = [];
    public IReadOnlyList<TrackSyncDto> Tracks { get; set; } = [];

    /// <summary>Highest <c>ServerVersion</c> contained in this page.</summary>
    public long NextVersion { get; set; }

    /// <summary>True when more rows remain beyond <see cref="NextVersion"/>.</summary>
    public bool HasMore { get; set; }
}
