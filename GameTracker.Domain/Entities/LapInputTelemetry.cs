namespace GameTracker.Domain.Entities;

/// <summary>
/// Per-lap input traces. Stored as a single Brotli-compressed columnar blob
/// (all throttle, then all brake, then all steering) plus a decimated preview so the
/// common-case chart render needs no decompression. See CHOICES AND REASONS.md.
/// </summary>
public class LapInputTelemetry
{
    public Guid Id { get; set; }

    public Guid LapId { get; set; }

    public Lap? Lap { get; set; }

    /// <summary>Number of samples per channel in <see cref="CompressedChannels"/>.</summary>
    public int SampleCount { get; set; }

    /// <summary>Sample rate the lap was captured at, in Hz.</summary>
    public int SampleRateHz { get; set; }

    /// <summary>Brotli-compressed columnar float32 payload: throttle, then brake, then steering.</summary>
    public byte[] CompressedChannels { get; set; } = [];

    /// <summary>Min/max decimated preview (~500 samples per channel), same columnar order, uncompressed.</summary>
    public byte[] Preview { get; set; } = [];

    public int PreviewSampleCount { get; set; }
}
