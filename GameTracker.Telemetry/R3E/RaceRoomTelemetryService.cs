using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using GameTracker.Telemetry.R3E.Data;

namespace GameTracker.Telemetry.R3E;

/// <summary>
/// Low-level accessor for the RaceRoom Racing Experience shared memory region.
/// </summary>
/// <remarks>
/// The mapped region only exists while the game is running AND the player is in a
/// session. Sitting in the menus is not enough - the region is created on session
/// entry and torn down on exit, so every read must tolerate its absence.
/// </remarks>
public sealed class RaceRoomTelemetryService : IDisposable
{
    /// <summary>
    /// The name of the memory-mapped region created by RaceRoom.
    /// </summary>
    /// <remarks>
    /// Sourced from <see cref="Constant.SharedMemoryName"/> rather than duplicated,
    /// so there is a single point of truth if the game ever renames it.
    /// </remarks>
    public const string SharedMemoryName = Constant.SharedMemoryName;

    private MemoryMappedFile? _mappedFile;
    private MemoryMappedViewAccessor? _accessor;
    private bool _disposed;

    /// <summary>
    /// True when the shared memory region is currently open.
    /// </summary>
    public bool IsConnected => _accessor is not null;

    /// <summary>
    /// The byte length of the mapped view, or null when not connected.
    /// </summary>
    /// <remarks>
    /// This value is rounded up by the OS to the nearest page boundary (4096 bytes),
    /// so it will almost never equal <c>Marshal.SizeOf&lt;Shared&gt;()</c> exactly.
    /// Compare with a page-sized tolerance, never for equality.
    /// </remarks>
    public long? MappedViewCapacity => _accessor?.Capacity;

    /// <summary>
    /// Attempts to open the shared memory region.
    /// </summary>
    /// <returns>True if connected, false if the game is not running or not in a session.</returns>
    public bool TryConnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsConnected)
        {
            return true;
        }

        try
        {
            _mappedFile = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            _accessor = _mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            // Expected whenever the game is closed or the player is sitting in the menus.
            Disconnect();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Disconnect();
            return false;
        }
    }

    /// <summary>
    /// Reads the interface version from the head of the mapped region.
    /// </summary>
    /// <remarks>
    /// VersionMajor and VersionMinor occupy the first two 32-bit slots of the struct and
    /// have never moved across layout revisions, so this is safe to call even when the
    /// rest of the local struct definition is stale. This is what makes a meaningful
    /// version gate possible in the first place.
    /// </remarks>
    public (int Major, int Minor)? TryReadVersion()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_accessor is null)
        {
            return null;
        }

        var major = _accessor.ReadInt32(0);
        var minor = _accessor.ReadInt32(sizeof(int));
        return (major, minor);
    }

    /// <summary>
    /// Reads the full shared memory struct.
    /// </summary>
    /// <remarks>
    /// Callers must validate the interface version before trusting this data. Reading a
    /// struct whose layout does not match the running game yields silently wrong values,
    /// which is worse than failing outright.
    /// </remarks>
    public unsafe Shared? TryReadShared()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_accessor is null)
        {
            return null;
        }

        var size = SharedStructSize;
        if (_accessor.Capacity < size)
        {
            return null;
        }

        // Shared contains [MarshalAs(ByValArray)] managed arrays, so it is not an
        // unmanaged type: MemoryMappedViewAccessor.Read<T> rejects it outright.
        // Marshal.PtrToStructure understands the marshalling attributes and is the
        // only correct way to project this layout.
        var handle = _accessor.SafeMemoryMappedViewHandle;
        byte* pointer = null;

        try
        {
            handle.AcquirePointer(ref pointer);
            if (pointer is null)
            {
                return null;
            }

            return Marshal.PtrToStructure<Shared>((nint)(pointer + _accessor.PointerOffset));
        }
        finally
        {
            if (pointer is not null)
            {
                handle.ReleasePointer();
            }
        }
    }

    /// <summary>
    /// The managed size of the local <see cref="Shared"/> struct definition.
    /// </summary>
    public static int SharedStructSize => Marshal.SizeOf<Shared>();

    /// <summary>
    /// Releases the mapped view, e.g. when the game exits mid-session.
    /// </summary>
    public void Disconnect()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mappedFile?.Dispose();
        _mappedFile = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Disconnect();
        _disposed = true;
    }
}
