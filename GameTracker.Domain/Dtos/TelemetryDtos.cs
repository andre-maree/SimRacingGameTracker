using GameTracker.Domain.Enums;

namespace GameTracker.Domain.Dtos;

/// <summary>A session header uploaded by the client via <c>POST /api/sessions</c>.</summary>
public class SessionUploadDto
{
    public Guid Id { get; set; }
    public int GameId { get; set; }
    public int CarExternalId { get; set; }
    public int TrackExternalId { get; set; }
    public SessionType SessionType { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public SessionEndReason? EndReason { get; set; }
}

/// <summary>A single lap result inside a telemetry batch.</summary>
public class TelemetryRecordDto
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int GameId { get; set; }
    public int CarExternalId { get; set; }
    public int TrackExternalId { get; set; }
    public int LapNumber { get; set; }
    public double? LapTime { get; set; }
    public bool IsValid { get; set; }
    public DateTime RecordedAtUtc { get; set; }
}

/// <summary>Payload for <c>POST /api/telemetry/batch</c>. Idempotent on each record's GUID.</summary>
public class TelemetryBatchRequest
{
    public IReadOnlyList<TelemetryRecordDto> Records { get; set; } = [];
}

/// <summary>Result of a batch upload.</summary>
public class TelemetryBatchResponse
{
    public int Accepted { get; set; }

    /// <summary>Records already present server-side and therefore skipped.</summary>
    public int Duplicates { get; set; }
}
