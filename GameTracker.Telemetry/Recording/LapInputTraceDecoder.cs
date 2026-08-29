using System.Buffers.Binary;
using System.IO.Compression;

namespace GameTracker.Telemetry.Recording;

/// <summary>One decoded input trace: three equal-length channels for a lap.</summary>
/// <remarks>
/// Steering keeps its own array rather than being folded into a single interleaved series
/// because it is charted on a different axis (-1..1) to the pedals (0..1).
/// </remarks>
public sealed record DecodedInputTrace(
    float[] Throttle,
    float[] Brake,
    float[] Steering,
    int SampleRateHz)
{
    public int SampleCount => Throttle.Length;

    /// <summary>
    /// Elapsed seconds for the sample at <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// Derived from the sample rate rather than stored per sample: the capture is a fixed
    /// cadence poll, so a timestamp column would triple the payload to record an
    /// arithmetic progression.
    /// </remarks>
    public double TimeAt(int index) => SampleRateHz <= 0 ? 0 : (double)index / SampleRateHz;
}

/// <summary>
/// Reads back the columnar payloads written by <see cref="LapInputTraceBuffer"/>.
/// </summary>
/// <remarks>
/// The counterpart to the encoder, and deliberately in the same project so the layout is
/// defined in exactly one place. A blob whose write and read sides can drift apart would
/// fail silently — decoding garbage as valid floats produces a plausible-looking but wrong
/// chart rather than an exception.
/// </remarks>
public static class LapInputTraceDecoder
{
    /// <summary>
    /// Decodes the cheap preview, which needs no decompression.
    /// </summary>
    /// <remarks>
    /// This is the common path: a lap list renders many small charts, and inflating every
    /// full trace to draw a thumbnail would be wasteful. The preview is already stored at
    /// chart resolution.
    /// </remarks>
    public static DecodedInputTrace? DecodePreview(byte[] preview, int previewSampleCount, int sampleRateHz)
    {
        if (previewSampleCount <= 0)
        {
            return null;
        }

        // The preview is decimated, so its effective rate is not the capture rate. Callers
        // chart it against sample index, and TimeAt would otherwise lie.
        var channelBytes = previewSampleCount * sizeof(float);

        // Guards against a truncated or mislabelled blob before any indexing happens.
        if (preview.Length < channelBytes * 3)
        {
            return null;
        }

        // The preview stays float32: it is ~500 samples per channel and uncompressed, so
        // quantising it would save a couple of KB while adding a second format to maintain.
        return new DecodedInputTrace(
            ReadPreviewChannel(preview, 0, previewSampleCount),
            ReadPreviewChannel(preview, channelBytes, previewSampleCount),
            ReadPreviewChannel(preview, channelBytes * 2, previewSampleCount),
            SampleRateHz: 0);
    }

    /// <summary>
    /// Inflates and decodes the full-resolution trace.
    /// </summary>
    /// <remarks>
    /// Only called on demand — when the user zooms or overlays two laps — because this is
    /// the expensive path that the preview exists to avoid.
    /// </remarks>
    public static DecodedInputTrace? DecodeFull(byte[] compressedChannels, int sampleCount, int sampleRateHz)
    {
        if (sampleCount <= 0 || compressedChannels.Length == 0)
        {
            return null;
        }

        var expectedBytes = checked(sampleCount * 3 * sizeof(ushort));

        using var source = new MemoryStream(compressedChannels);
        using var brotli = new BrotliStream(source, CompressionMode.Decompress);

        var payload = new byte[expectedBytes];

        // ReadExactly, not Read: a short read would silently leave the tail of the buffer
        // as zeros, which decodes as a lap that ends with the throttle shut.
        try
        {
            brotli.ReadExactly(payload, 0, expectedBytes);
        }
        catch (EndOfStreamException)
        {
            // The blob does not match its declared sample count, so it cannot be trusted.
            // Returning null lets the caller fall back to the preview.
            return null;
        }

        var channelBytes = sampleCount * sizeof(ushort);

        return new DecodedInputTrace(
            ReadChannel(payload, 0, sampleCount, signed: false),
            ReadChannel(payload, channelBytes, sampleCount, signed: false),
            ReadChannel(payload, channelBytes * 2, sampleCount, signed: true),
            sampleRateHz);
    }

    /// <summary>
    /// Reads one delta-encoded quantised channel back to floats.
    /// </summary>
    /// <remarks>
    /// The running total uses unchecked <c>ushort</c> arithmetic so it wraps exactly as the
    /// encoder's subtraction did; without the matching wrap, any channel crossing the
    /// 16-bit boundary would decode as a wild spike.
    /// </remarks>
    private static float[] ReadChannel(byte[] source, int offset, int sampleCount, bool signed)
    {
        var values = new float[sampleCount];
        ushort running = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            running = (ushort)(running + BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset)));
            offset += sizeof(ushort);

            var normalised = (float)running / ushort.MaxValue;

            // Steering is mapped back from the shared unsigned range to -1..1.
            values[i] = signed ? (normalised * 2f) - 1f : normalised;
        }

        return values;
    }

    /// <summary>Reads a float32 preview channel, which is stored uncompressed and unquantised.</summary>
    private static float[] ReadPreviewChannel(byte[] source, int offset, int sampleCount)
    {
        var values = new float[sampleCount];

        // Little-endian explicitly, mirroring the encoder: the blob is written on the
        // desktop client and may be read back on the server, so it must not depend on the
        // architecture of whichever machine happens to touch it.
        for (var i = 0; i < sampleCount; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(source.AsSpan(offset));
            offset += sizeof(float);
        }

        return values;
    }
}
