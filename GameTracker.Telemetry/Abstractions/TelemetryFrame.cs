using GameTracker.Domain.Enums;

namespace GameTracker.Telemetry.Abstractions;

/// <summary>
/// A game-agnostic snapshot of one polled telemetry sample.
/// </summary>
/// <remarks>
/// This is the boundary between interop and the rest of the app: the state machine and
/// the recorder only ever see this type, never the raw <c>Shared</c> struct. Fields are
/// nullable wherever RaceRoom uses its <c>-1</c> "not available" sentinel, so an
/// unavailable reading can never be mistaken for a real one. Shaped against the struct
/// layout confirmed by the step 6 spike (interface 3.5).
/// </remarks>
public sealed record TelemetryFrame
{
    /// <summary>Wall-clock time the frame was polled, used for session timestamps.</summary>
    public required DateTime CapturedAtUtc { get; init; }

    /// <summary>
    /// The game's monotonically increasing simulation clock. A decrease is the primary
    /// signal that the session was restarted.
    /// </summary>
    /// <remarks>
    /// Nullable because RaceRoom blanks the player block to its <c>-1</c> sentinel whenever
    /// the car is not under the player's control. Treating that -1 as a real reading makes
    /// the clock appear to jump backwards by the whole elapsed session, which the state
    /// machine would read as a restart and use to discard the lap in progress.
    /// </remarks>
    public required double? GameSimulationTime { get; init; }

    /// <summary>True when the player is sitting in the menus rather than on track.</summary>
    public required bool GameInMenus { get; init; }

    /// <summary>
    /// True while the game is paused. RaceRoom also raises <see cref="GameInMenus"/> for the
    /// pause overlay, so this is what distinguishes a paused session from a quit one.
    /// </summary>
    public bool GamePaused { get; init; }

    /// <summary>
    /// True while a replay is being watched, which likewise raises <see cref="GameInMenus"/>
    /// without the session having ended.
    /// </summary>
    public bool GameInReplay { get; init; }

    public required SessionType SessionType { get; init; }

    /// <summary>Raw R3E session phase; null when unavailable.</summary>
    public int? SessionPhase { get; init; }

    /// <summary>R3E <c>VehicleInfo.ModelId</c>. Joins to <c>Car.ExternalId</c>.</summary>
    public int? CarExternalId { get; init; }

    /// <summary>R3E <c>LayoutId</c>. Joins to <c>Track.ExternalId</c>.</summary>
    public int? TrackExternalId { get; init; }

    public int? NumCars { get; init; }

    public int? CompletedLaps { get; init; }

    /// <summary>Position around the lap, 0..1. Used to detect lap rollover.</summary>
    public double? LapDistanceFraction { get; init; }

    /// <summary>Current lap time in seconds; null until the game reports one.</summary>
    public double? LapTimeCurrent { get; init; }

    public double? LapTimeBest { get; init; }

    /// <summary>
    /// The game's own time for the lap just completed. Preferred over the last observed
    /// <see cref="LapTimeCurrent"/> when closing a lap: polling at 60 Hz means the current
    /// time is always sampled slightly before the line, so it under-reports by up to a frame.
    /// </summary>
    public double? LapTimePrevious { get; init; }

    /// <summary>Cumulative sector times for the lap just completed, in seconds.</summary>
    public double? PreviousSector1 { get; init; }

    public double? PreviousSector2 { get; init; }

    public double? PreviousSector3 { get; init; }

    /// <summary>Cumulative sector times for the current lap, in seconds.</summary>
    public double? Sector1 { get; init; }

    public double? Sector2 { get; init; }

    public double? Sector3 { get; init; }

    /// <summary>
    /// Whether the game currently considers the lap valid. The state machine latches
    /// this: once false, the lap stays invalid until it completes.
    /// </summary>
    public bool? CurrentLapValid { get; init; }

    public bool InPitLane { get; init; }

    public int? PitState { get; init; }

    public double? SpeedMetresPerSecond { get; init; }

    public int? Gear { get; init; }

    public double? EngineRps { get; init; }

    public double? FuelLeft { get; init; }

    /// <summary>Raw throttle input, 0..1. Captured per frame for the lap input trace.</summary>
    public float Throttle { get; init; }

    /// <summary>Raw brake input, 0..1.</summary>
    public float Brake { get; init; }

    /// <summary>Raw steering input, -1..1.</summary>
    public float Steering { get; init; }
}
