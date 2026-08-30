using System.Runtime.CompilerServices;
using GameTracker.Domain.Enums;
using GameTracker.Telemetry.Abstractions;
using GameTracker.Telemetry.R3E.Data;
using Microsoft.Extensions.Logging;

namespace GameTracker.Telemetry.R3E;

/// <summary>
/// Polls RaceRoom's shared memory and yields <see cref="TelemetryFrame"/> values.
/// </summary>
/// <remarks>
/// The stream is deliberately endless: RaceRoom creates the mapped region on session entry
/// and tears it down on exit, so "not connected" is a normal, recurring state rather than a
/// failure. Terminating the stream would force every consumer to implement its own restart
/// logic, so instead the loop backs off and reconnects.
/// </remarks>
public sealed class SharedMemoryTelemetrySource : ITelemetrySource, IDisposable
{
    /// <summary>
    /// 60 Hz. Matched to the game's own update rate: polling faster only re-reads identical
    /// bytes, and polling slower risks missing the frame in which a lap rolls over.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000.0 / 60);

    /// <summary>
    /// Retry delay while the game is closed or in the menus. One second, because this is
    /// the idle state for most of the application's life and a 60 Hz spin on
    /// <c>OpenExisting</c> would burn CPU for nothing.
    /// </summary>
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(1);

    private readonly RaceRoomTelemetryService _sharedMemory;
    private readonly ILogger<SharedMemoryTelemetrySource> _logger;

    /// <summary>
    /// Latched once the version gate rejects the running game. Without the latch the loop
    /// would log the same fatal mismatch 60 times a second.
    /// </summary>
    private bool _incompatibleReported;

    public SharedMemoryTelemetrySource(ILogger<SharedMemoryTelemetrySource> logger)
    {
        _logger = logger;
        _sharedMemory = new RaceRoomTelemetryService();
    }

    public bool IsConnected => _sharedMemory.IsConnected;

    /// <summary>True once the running game has passed the version gate at least once.</summary>
    public bool IsVersionValidated { get; private set; }

    public async IAsyncEnumerable<TelemetryFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_sharedMemory.TryConnect())
            {
                // Expected constantly: the game is closed, or the player is in the menus.
                IsVersionValidated = false;
                await DelayAsync(ReconnectInterval, cancellationToken);
                continue;
            }

            if (!IsVersionValidated && !ValidateVersion())
            {
                // Refuse to read. Disconnecting also means that if the user switches to a
                // compatible build, the next reconnect re-runs the gate cleanly.
                _sharedMemory.Disconnect();
                await DelayAsync(ReconnectInterval, cancellationToken);
                continue;
            }

            var shared = _sharedMemory.TryReadShared();

            if (shared is null)
            {
                // The region vanished between connecting and reading - the game exited.
                _logger.LogInformation("RaceRoom shared memory became unavailable; awaiting reconnect.");
                _sharedMemory.Disconnect();
                IsVersionValidated = false;
                continue;
            }

            yield return Map(shared.Value);

            await DelayAsync(PollInterval, cancellationToken);
        }
    }

    private bool ValidateVersion()
    {
        var version = _sharedMemory.TryReadVersion();
        var capacity = _sharedMemory.MappedViewCapacity;

        if (version is null || capacity is null)
        {
            return false;
        }

        var result = SharedMemoryVersionGate.Validate(version.Value.Major, version.Value.Minor, capacity.Value);

        if (!result.IsCompatible)
        {
            if (!_incompatibleReported)
            {
                // Error, not Warning: recording is disabled entirely, and reading anyway
                // would produce plausible but wrong lap data.
                _logger.LogError(
                    "Refusing to read RaceRoom telemetry: {Reason} Recording is disabled until a compatible game version is detected.",
                    result.Reason);

                _incompatibleReported = true;
            }

            return false;
        }

        _logger.LogInformation("RaceRoom telemetry version gate passed: {Reason}", result.Reason);
        _incompatibleReported = false;
        IsVersionValidated = true;
        return true;
    }

    /// <summary>
    /// Projects the raw struct onto the game-agnostic frame, funnelling every value through
    /// <see cref="R3EValue"/> so RaceRoom's <c>-1</c> "unavailable" sentinel can never be
    /// recorded as a real reading.
    /// </summary>
    private static TelemetryFrame Map(Shared shared) => new()
    {
        CapturedAtUtc = DateTime.UtcNow,
        GameSimulationTime = R3EValue.ToNullable(shared.Player.GameSimulationTime),
        GameInMenus = R3EValue.ToFlag(shared.GameInMenus),
        GamePaused = R3EValue.ToFlag(shared.GamePaused),
        GameInReplay = R3EValue.ToFlag(shared.GameInReplay),
        GamePlayerInGarage = R3EValue.ToFlag(shared.GamePlayerInGarage),
        SessionType = MapSessionType(shared.SessionType),
        SessionPhase = R3EValue.ToNullable(shared.SessionPhase),

        // ModelId and LayoutId are what the catalogue joins on; they are the whole reason
        // Car.ExternalId and Track.ExternalId exist.
        CarExternalId = R3EValue.ToNullable(shared.VehicleInfo.ModelId),
        TrackExternalId = R3EValue.ToNullable(shared.LayoutId),
        NumCars = R3EValue.ToNullable(shared.NumCars),

        CompletedLaps = R3EValue.ToNullable(shared.CompletedLaps),
        LapDistanceFraction = R3EValue.ToNullable(shared.LapDistanceFraction),
        LapTimeCurrent = R3EValue.ToNullable(shared.LapTimeCurrentSelf),
        LapTimeBest = R3EValue.ToNullable(shared.LapTimeBestSelf),

        LapTimePrevious = R3EValue.ToNullable(shared.LapTimePreviousSelf),
        PreviousSector1 = R3EValue.ToNullable(shared.SectorTimesPreviousSelf.Sector1),
        PreviousSector2 = R3EValue.ToNullable(shared.SectorTimesPreviousSelf.Sector2),
        PreviousSector3 = R3EValue.ToNullable(shared.SectorTimesPreviousSelf.Sector3),

        Sector1 = R3EValue.ToNullable(shared.SectorTimesCurrentSelf.Sector1),
        Sector2 = R3EValue.ToNullable(shared.SectorTimesCurrentSelf.Sector2),
        Sector3 = R3EValue.ToNullable(shared.SectorTimesCurrentSelf.Sector3),

        CurrentLapValid = R3EValue.ToNullableFlag(shared.CurrentLapValid),

        // Defaults to false when unavailable: assuming "not in the pits" keeps a stint open
        // rather than inventing a pit stop from a missing reading.
        InPitLane = R3EValue.ToFlag(shared.InPitlane),
        PitState = R3EValue.ToNullable(shared.PitState),

        SpeedMetresPerSecond = R3EValue.ToNullable(shared.CarSpeed),

        // Gear uses -2 for "not available" and -1 for reverse, so the shared -1 sentinel
        // helper would wrongly discard reverse.
        Gear = shared.Gear <= -2 ? null : shared.Gear,

        EngineRps = R3EValue.ToNullable(shared.EngineRps),
        FuelLeft = R3EValue.ToNullable(shared.FuelLeft),

        // Raw pedal/steering inputs, before any assists, which is what a driver trace needs.
        Throttle = shared.ThrottleRaw,
        Brake = shared.BrakeRaw,
        Steering = shared.SteerInputRaw
    };

    private static SessionType MapSessionType(int sessionType) => sessionType switch
    {
        0 => SessionType.Practice,
        1 => SessionType.Qualify,
        2 => SessionType.Race,
        3 => SessionType.Warmup,
        _ => SessionType.Unknown
    };

    /// <summary>
    /// Cancellation during a poll delay is an ordinary shutdown, not an error, so the
    /// exception is swallowed and the loop condition ends the stream.
    /// </summary>
    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose() => _sharedMemory.Dispose();
}
