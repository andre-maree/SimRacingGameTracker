using GameTracker.Domain.Enums;

namespace GameTracker.Domain.Entities;

/// <summary>
/// One continuous on-track outing, from entering a session to leaving it.
/// Identified by a client-generated GUID so uploads are idempotent.
/// </summary>
public class Session
{
    public Guid Id { get; set; }

    public int GameId { get; set; }

    /// <summary>R3E <c>ModelId</c> of the car driven. Resolved against <see cref="Car.ExternalId"/>.</summary>
    public int CarExternalId { get; set; }

    /// <summary>R3E <c>LayoutId</c> of the layout driven. Resolved against <see cref="Track.ExternalId"/>.</summary>
    public int TrackExternalId { get; set; }

    public SessionType SessionType { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    public SessionEndReason? EndReason { get; set; }

    /// <summary>Owner of the session, stamped server-side on upload. Never trusted from the client.</summary>
    public string? UserId { get; set; }

    public ICollection<Stint> Stints { get; set; } = [];
}
