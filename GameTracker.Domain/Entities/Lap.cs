namespace GameTracker.Domain.Entities;

/// <summary>
/// A completed lap. Invalid laps are persisted with <see cref="LapTime"/> null rather
/// than being discarded, so lap counts always reconcile with the game.
/// </summary>
public class Lap
{
    public Guid Id { get; set; }

    public Guid StintId { get; set; }

    public Stint? Stint { get; set; }

    public int LapNumber { get; set; }

    /// <summary>Lap time in seconds. Null when the game never reported a valid time.</summary>
    public double? LapTime { get; set; }

    public double? Sector1 { get; set; }

    public double? Sector2 { get; set; }

    public double? Sector3 { get; set; }

    /// <summary>Latched for the whole lap: once invalidated, it stays invalid.</summary>
    public bool IsValid { get; set; }

    public bool IsPitLap { get; set; }

    public DateTime CompletedAtUtc { get; set; }

    public LapInputTelemetry? InputTelemetry { get; set; }
}
