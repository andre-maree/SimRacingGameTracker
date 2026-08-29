namespace GameTracker.Domain.Entities;

/// <summary>
/// A summarised lap result uploaded to the server. Keyed by the client-generated
/// <see cref="Id"/> so re-sending a batch is idempotent.
/// </summary>
public class TelemetryRecord
{
    public Guid Id { get; set; }

    public int GameId { get; set; }

    public int CarExternalId { get; set; }

    public int TrackExternalId { get; set; }

    public Guid SessionId { get; set; }

    public int LapNumber { get; set; }

    public double? LapTime { get; set; }

    public bool IsValid { get; set; }

    public DateTime RecordedAtUtc { get; set; }

    /// <summary>Stamped server-side from the authenticated principal on upload.</summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Client-only upload marker: null means the record is still queued. The server
    /// ignores this column, since server-side rows are uploaded by definition.
    /// </summary>
    public DateTime? UploadedAtUtc { get; set; }
}
