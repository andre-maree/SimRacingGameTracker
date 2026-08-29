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
/// traces are smooth and autocorrelated: keeping like values adjacent is what lets Brotli
/// reduce them to tens of KB. Interleaving the channels would break that locality.
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

        var payload = new byte[count * 3 * sizeof(float)];
        WriteChannel(payload, 0, _throttle);
        WriteChannel(payload, count * sizeof(float), _brake);
        WriteChannel(payload, count * 2 * sizeof(float), _steering);

        return new EncodedInputTrace(
            count,
            _sampleRateHz,
            Compress(payload),
            BuildPreview(count, out var previewSamples),
            previewSamples);
    }

    private static void WriteChannel(byte[] destination, int offset, List<float> samples)
    {
        // Little-endian explicitly rather than BitConverter: the blob is written by the
        // desktop client and read back by the server, so the encoding must not depend on
        // the architecture of whichever machine happens to touch it.
        foreach (var sample in samples)
        {
            BinaryPrimitives.WriteSingleLittleEndian(destination.AsSpan(offset), sample);
            offset += sizeof(float);
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
