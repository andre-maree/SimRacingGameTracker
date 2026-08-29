using System.Runtime.InteropServices;
using GameTracker.Telemetry.R3E.Data;

namespace GameTracker.Telemetry.R3E;

/// <summary>The outcome of validating the running game's shared memory against our layout.</summary>
public sealed record VersionGateResult(bool IsCompatible, string Reason)
{
    public static VersionGateResult Compatible(string reason) => new(true, reason);

    public static VersionGateResult Incompatible(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether the running game's shared memory can be safely projected onto our
/// <see cref="Shared"/> struct definition.
/// </summary>
/// <remarks>
/// This gate exists because the failure mode it prevents is silent. If RaceRoom changes
/// its layout and we read anyway, <c>Marshal.PtrToStructure</c> still succeeds — it simply
/// reinterprets whatever bytes are there, and we record plausible-looking but wrong lap
/// times. Refusing to start is the only honest response: no data is far better than
/// corrupt data that gets uploaded and trusted.
/// </remarks>
public static class SharedMemoryVersionGate
{
    /// <summary>Interface major version this build was written against (step 6 spike: 3).</summary>
    public const int ExpectedMajor = (int)Constant.VersionMajor.R3E_VERSION_MAJOR;

    /// <summary>Interface minor version this build was written against (step 6 spike: 5).</summary>
    public const int ExpectedMinor = (int)Constant.VersionMinor.R3E_VERSION_MINOR;

    /// <summary>
    /// Windows rounds a mapped view up to the next 4 KiB page, so the mapped capacity is
    /// almost never equal to the struct size. The step 6 spike measured 43,996 bytes of
    /// struct inside a 45,056-byte view — a 1,060-byte gap that is padding, not a mismatch.
    /// </summary>
    private const long PageSize = 4096;

    /// <summary>
    /// Validates the reported interface version and the mapped region size.
    /// </summary>
    public static VersionGateResult Validate(int major, int minor, long mappedCapacity)
    {
        // Major is the breaking-change signal: a different major means the layout has been
        // restructured and none of our offsets can be trusted.
        if (major != ExpectedMajor)
        {
            return VersionGateResult.Incompatible(
                $"Shared memory interface major version {major}.{minor} does not match the " +
                $"expected {ExpectedMajor}.{ExpectedMinor}. The struct layout has changed.");
        }

        // A *lower* minor means the game predates fields we expect to exist, so reading
        // them would run past the data the game actually publishes.
        if (minor < ExpectedMinor)
        {
            return VersionGateResult.Incompatible(
                $"Shared memory interface {major}.{minor} is older than the expected " +
                $"{ExpectedMajor}.{ExpectedMinor}; expected fields may be absent.");
        }

        var structSize = Marshal.SizeOf<Shared>();

        // The view must at least contain our struct. Anything smaller means a read would
        // walk off the end of the published region.
        if (mappedCapacity < structSize)
        {
            return VersionGateResult.Incompatible(
                $"Mapped region is {mappedCapacity} bytes, smaller than the {structSize}-byte " +
                "struct this build expects.");
        }

        // Compared with page tolerance, never for equality: the OS pads the view.
        var slack = mappedCapacity - structSize;

        if (slack >= PageSize)
        {
            return VersionGateResult.Incompatible(
                $"Mapped region is {mappedCapacity} bytes against a {structSize}-byte struct " +
                $"({slack} bytes of slack, more than one {PageSize}-byte page). The game is " +
                "likely publishing a larger, different layout.");
        }

        // A higher minor is additive: fields are appended, so our prefix still reads
        // correctly. Worth logging, because it means the game is newer than this build.
        var note = minor > ExpectedMinor
            ? $" (game reports {major}.{minor}, newer than the expected {ExpectedMajor}.{ExpectedMinor}; " +
              "additional fields will be ignored)"
            : string.Empty;

        return VersionGateResult.Compatible(
            $"Shared memory interface {major}.{minor} accepted; {structSize}-byte struct in a " +
            $"{mappedCapacity}-byte view ({slack} bytes of page padding).{note}");
    }
}
