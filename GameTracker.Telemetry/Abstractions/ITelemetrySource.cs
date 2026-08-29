namespace GameTracker.Telemetry.Abstractions;

/// <summary>
/// A pollable source of telemetry frames.
/// </summary>
/// <remarks>
/// Abstracting the source keeps the recorder testable without RaceRoom running: a
/// replay or fake source can satisfy this interface. Implementations must tolerate the
/// game starting and stopping at any time and simply resume when it returns.
/// </remarks>
public interface ITelemetrySource
{
    /// <summary>True while the underlying source is readable.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Streams frames until cancellation. The stream does not terminate when the game
    /// closes; it pauses and resumes on reconnect, so callers keep a single subscription
    /// for the lifetime of the app.
    /// </summary>
    IAsyncEnumerable<TelemetryFrame> ReadFramesAsync(CancellationToken cancellationToken);
}
