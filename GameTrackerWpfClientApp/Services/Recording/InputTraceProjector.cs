using System;
using System.Collections.Generic;
using GameTracker.Telemetry.Recording;

namespace GameTrackerWpfClientApp.Services.Recording;

/// <summary>One plotted sample of a lap's input trace.</summary>
/// <remarks>
/// Percentages rather than the stored 0..1 floats: the chart axis and tooltips read in the
/// units a driver actually talks in, and doing the conversion once here keeps it out of
/// the markup.
/// </remarks>
public sealed record InputTracePoint(
    double Position,
    double Throttle,
    double Brake,
    double Steering);

/// <summary>
/// A decoded trace flattened into chart-ready points.
/// </summary>
public sealed record InputTraceSeries(
    string Label,
    IReadOnlyList<InputTracePoint> Points,
    bool IsTimeAxis)
{
    /// <summary>Axis title, since the preview and full traces are plotted against different units.</summary>
    public string AxisTitle => IsTimeAxis ? "Lap time (s)" : "Lap progress (%)";
}

/// <summary>
/// Converts <see cref="DecodedInputTrace"/> into series the chart can bind to.
/// </summary>
/// <remarks>
/// Kept out of the component so the axis decision is made in one testable place. The
/// awkward part is that a preview has no meaningful sample rate: it is min/max decimated,
/// so its points are not evenly spaced in time and plotting them against seconds would
/// misrepresent where in the lap an input happened.
/// </remarks>
public static class InputTraceProjector
{
    /// <summary>
    /// Beyond this, plotting every sample costs far more than it shows: an SVG chart a few
    /// hundred pixels wide cannot resolve more points than it has pixels.
    /// </summary>
    private const int MaxPlottedPoints = 1200;

    public static InputTraceSeries Project(DecodedInputTrace trace, string label)
    {
        // A full trace has a real capture rate and is evenly spaced, so it can be charted
        // against elapsed seconds. A preview cannot: it is decimated, so it is charted
        // against percentage of lap progress instead of pretending to be a clock.
        var isTimeAxis = trace.SampleRateHz > 0;

        var stride = Math.Max(1, trace.SampleCount / MaxPlottedPoints);
        var points = new List<InputTracePoint>(trace.SampleCount / stride + 1);

        for (var i = 0; i < trace.SampleCount; i += stride)
        {
            points.Add(new InputTracePoint(
                isTimeAxis
                    ? trace.TimeAt(i)
                    : (double)i / Math.Max(1, trace.SampleCount - 1) * 100.0,

                // Steering stays signed: it is a direction, and folding it to a magnitude
                // would hide which way the driver was turning.
                trace.Throttle[i] * 100.0,
                trace.Brake[i] * 100.0,
                trace.Steering[i] * 100.0));
        }

        return new InputTraceSeries(label, points, isTimeAxis);
    }
}
