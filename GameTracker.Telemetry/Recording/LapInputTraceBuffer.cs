using System.Buffers.Binary;
using System.IO.Compression;

namespace GameTracker.Telemetry.Recording;

/// <summary>The encoded result for one lap's input trace.</summary>
public sealed record EncodedInputTrace(
    int SampleCount,
    int SampleRateHz,
    byte[] CompressedChannels,
    byte[] Preview,
    int PreviewSampleCount);

/// <summary>
/// Accumulates per-frame driver inputs for a lap and encodes them on completion.
/// </summary>
/// <remarks>
/// One row per lap instead of one per sample: a 90-second lap at 60 Hz across three
/// channels is ~16,000 samples, and a row each would mean SQLite write amplification and
/// index maintenance on the hot recording path. See CHOICES AND REASONS.md, Part 5.
/// <para>
/// The layout is columnar (all throttle, then all brake, then all steering) because pedal
/// traces are smooth and autocorrelated: keeping like values adjacent is what lets the
/// delta encoding and Brotli work. Interleaving the channels would break that locality.
/// </para>
/// <para>
/// Channels are stored as delta-encoded 16-bit quantised samples rather than float32.
/// Measured on a 90-second lap: float32 compressed to 48,126 bytes (74% of raw, since
/// mantissa noise is incompressible), while quantised deltas reach 11,990 bytes.
/// </para>
/// </remarks>
public sealed class LapInputTraceBuffer
{
    /// <summary>Target preview length per channel; enough resolution for a full-lap chart.</summary>
    private const int PreviewSamplesPerChannel = 500;

    private readonly List<float> _throttle = [];
    private readonly List<float> _brake = [];
    private readonly List<float> _steering = [];
    private readonly int _sampleRateHz;

    public LapInputTraceBuffer(int sampleRateHz) => _sampleRateHz = sampleRateHz;

    public int SampleCount => _throttle.Count;

    public void Add(float throttle, float brake, float steering)
    {
        _throttle.Add(throttle);
        _brake.Add(brake);
        _steering.Add(steering);
    }

    public void Clear()
    {
        _throttle.Clear();
        _brake.Clear();
        _steering.Clear();
    }

    /// <summary>
    /// Encodes the buffered lap, or returns null when nothing was captured.
    /// </summary>
    /// <remarks>
    /// Called on the consumer task, never the poll loop: compression must not be allowed
    /// to delay the next telemetry read.
    /// </remarks>
    public EncodedInputTrace? Encode()
    {
        var count = _throttle.Count;

        if (count == 0)
        {
            return null;
        }

        // Two bytes per sample, not four: see WriteChannel for why float32 is the wrong
        // storage type here.
        var payload = new byte[count * 3 * sizeof(ushort)];
        WriteChannel(payload, 0, _throttle, signed: false);
        WriteChannel(payload, count * sizeof(ushort), _brake, signed: false);
        WriteChannel(payload, count * 2 * sizeof(ushort), _steering, signed: true);

        return new EncodedInputTrace(
            count,
            _sampleRateHz,
            Compress(payload),
            BuildPreview(count, out var previewSamples),
            previewSamples);
    }

    /// <summary>
    /// Writes one channel as delta-encoded unsigned 16-bit samples.
    /// </summary>
    /// <remarks>
    /// Float32 was measured at only 74% of raw after Brotli, because a mantissa's low bits
    /// are effectively random and incompressible. A pedal or wheel sensor has nowhere near
    /// 24 bits of real precision, so those bits were storing noise and defeating the
    /// compressor. Quantising to 16 bits gives a resolution of ~1.5e-5, roughly two orders
    /// of magnitude finer than any real input device, and costs nothing visible on a chart.
    /// <para>
    /// Deltas rather than absolute values because consecutive samples at 60 Hz barely
    /// differ: the differences cluster tightly around zero, which is exactly the
    /// distribution Brotli encodes well. Measured together at 11,990 bytes for a 90-second
    /// lap, against 48,126 for the float32 layout.
    /// </para>
    /// <para>
    /// Deltas wrap deliberately via unchecked <c>ushort</c> arithmetic; the decoder's
    /// matching wrap reconstructs the original value exactly, so this is lossless with
    /// respect to the quantised series.
    /// </para>
    /// </remarks>
    private static void WriteChannel(byte[] destination, int offset, List<float> samples, bool signed)
    {
        ushort previous = 0;

        foreach (var sample in samples)
        {
            // Steering is -1..1 and is mapped onto the same unsigned range as the pedals,
            // so one quantisation path serves all three channels.
            var normalised = signed ? (sample + 1f) / 2f : sample;
            var quantised = (ushort)Math.Clamp((int)MathF.Round(normalised * ushort.MaxValue), 0, ushort.MaxValue);

            // Little-endian explicitly rather than BitConverter: the blob is written by the
            // desktop client and read back by the server, so the encoding must not depend on
            // the architecture of whichever machine happens to touch it.
            BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset), (ushort)(quantised - previous));

            previous = quantised;
            offset += sizeof(ushort);
        }
    }

    private static byte[] Compress(byte[] payload)
    {
        using var output = new MemoryStream();

        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(payload);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Builds the uncompressed preview using min/max decimation.
    /// </summary>
    /// <remarks>
    /// Min/max per bucket rather than sampling every Nth value: plain sampling drops the
    /// brief full-throttle and full-brake spikes that are precisely what a driver looks
    /// for in the trace. Each bucket contributes two values, preserving the envelope.
    /// </remarks>
    private byte[] BuildPreview(int count, out int previewSampleCount)
    {
        var buckets = Math.Min(PreviewSamplesPerChannel / 2, count);
        buckets = Math.Max(buckets, 1);

        previewSampleCount = buckets * 2;

        var preview = new byte[previewSampleCount * 3 * sizeof(float)];
        var channelBytes = previewSampleCount * sizeof(float);

        DecimateChannel(preview, 0, _throttle, count, buckets);
        DecimateChannel(preview, channelBytes, _brake, count, buckets);
        DecimateChannel(preview, channelBytes * 2, _steering, count, buckets);

        return preview;
    }

    private static void DecimateChannel(
        byte[] destination,
        int offset,
        List<float> samples,
        int count,
        int buckets)
    {
        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var start = (int)((long)bucket * count / buckets);
            var end = (int)((long)(bucket + 1) * count / buckets);

            // Guard against a zero-width bucket when there are fewer samples than buckets.
            end = Math.Max(end, start + 1);

            var min = samples[start];
            var max = samples[start];

            for (var i = start + 1; i < end && i < count; i++)
            {
                min = Math.Min(min, samples[i]);
                max = Math.Max(max, samples[i]);
            }

            // Min before max keeps the pair in a fixed order, so the renderer can draw the
            // envelope without inspecting which value is which.
            BinaryPrimitives.WriteSingleLittleEndian(destination.AsSpan(offset), min);
            offset += sizeof(float);
            BinaryPrimitives.WriteSingleLittleEndian(destination.AsSpan(offset), max);
            offset += sizeof(float);
        }
    }
}
