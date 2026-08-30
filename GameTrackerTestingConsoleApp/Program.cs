using System.Runtime.InteropServices;
using GameTracker.Telemetry.R3E;
using GameTracker.Telemetry.R3E.Data;

namespace GameTrackerTestingConsoleApp;

/// <summary>
/// Verification spike for the RaceRoom shared memory layout.
/// </summary>
/// <remarks>
/// <para>
/// Purpose: confirm that the local <c>R3E.cs</c> struct definition actually matches the
/// installed build of RaceRoom before any domain or telemetry code is shaped around it.
/// The brief warns that the layout changed in a September 2025 update and will change
/// again, and a tool that silently misreads a struct is worse than one that won't start.
/// </para>
/// <para>
/// This runs in two independent parts. Part A reads the interface version and needs no
/// struct correctness at all - VersionMajor and VersionMinor sit at offsets 0 and 4 and
/// have never moved. Part B compares the managed struct size against the mapped region,
/// which is the cheap smoking gun for a layout divergence.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// The version this codebase's <c>R3E.cs</c> copy claims to implement.
    /// </summary>
    private const int ExpectedMajor = 3;
    private const int ExpectedMinor = 5;

    /// <summary>
    /// Windows rounds a mapped view's capacity up to the nearest page boundary, so the
    /// reported capacity is almost never exactly the struct size. Anything within one
    /// page is consistent with a matching layout.
    /// </summary>
    private const int PageSize = 4096;

    private static int Main()
    {
        Console.WriteLine("RaceRoom shared memory verification spike");
        Console.WriteLine("=========================================");
        Console.WriteLine();

        using var service = new RaceRoomTelemetryService();

        if (!service.TryConnect())
        {
            WriteFailure($"Shared memory region '{RaceRoomTelemetryService.SharedMemoryName}' was not found.");
            Console.WriteLine();
            Console.WriteLine("This is expected unless RaceRoom is running AND you are in a session.");
            Console.WriteLine("Sitting in the main menu is not enough - the region is created on session entry.");
            Console.WriteLine();
            Console.WriteLine("To run this spike:");
            Console.WriteLine("  1. Launch RaceRoom Racing Experience");
            Console.WriteLine("  2. Pick any free car and track");
            Console.WriteLine("  3. Enter a PRACTICE session and get on track");
            Console.WriteLine("  4. Leave the game running (alt-tab is fine) and re-run this app");
            Console.WriteLine();
            Console.WriteLine("If the region is still missing while genuinely on track, check whether your");
            Console.WriteLine("install gates shared memory behind a launcher setting.");
            return 1;
        }

        Console.WriteLine($"Connected to '{RaceRoomTelemetryService.SharedMemoryName}'.");
        Console.WriteLine();

        var versionOk = ReportVersion(service);
        Console.WriteLine();
        var sizeOk = ReportStructSize(service);
        Console.WriteLine();

        ReportSanityFields(service);
        Console.WriteLine();

        MonitorLive(service);
        Console.WriteLine();

        Console.WriteLine("=========================================");
        if (versionOk && sizeOk)
        {
            WriteSuccess("VERDICT: layout appears to match. Safe to proceed.");
            Console.WriteLine("Record the numbers above in Docs/CHOICES AND REASONS.md.");
            return 0;
        }

        WriteFailure("VERDICT: layout mismatch. Do NOT build domain types against this struct.");
        Console.WriteLine("Re-copy R3E.cs from https://github.com/kwstudios-sweden/r3e-api and re-run.");
        return 2;
    }

    /// <summary>
    /// Part A: read the interface version. Safe even against a completely stale struct.
    /// </summary>
    private static bool ReportVersion(RaceRoomTelemetryService service)
    {
        Console.WriteLine("--- Part A: interface version (struct-independent) ---");

        var version = service.TryReadVersion();
        if (version is null)
        {
            WriteFailure("Could not read version.");
            return false;
        }

        var (major, minor) = version.Value;
        Console.WriteLine($"  Reported by game : {major}.{minor}");
        Console.WriteLine($"  Expected by code : {ExpectedMajor}.{ExpectedMinor}");

        if (major == ExpectedMajor && minor == ExpectedMinor)
        {
            WriteSuccess("  MATCH");
            return true;
        }

        WriteFailure($"  MISMATCH - local R3E.cs claims {ExpectedMajor}.{ExpectedMinor}, game reports {major}.{minor}");
        return false;
    }

    /// <summary>
    /// Part B: compare the managed struct size against the mapped region.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT an equality check. The OS rounds the view capacity up to a page
    /// boundary, so exact equality would report a false failure on a perfectly good struct.
    /// </remarks>
    private static bool ReportStructSize(RaceRoomTelemetryService service)
    {
        Console.WriteLine("--- Part B: struct size vs mapped region ---");

        var structSize = RaceRoomTelemetryService.SharedStructSize;
        var capacity = service.MappedViewCapacity;

        if (capacity is null)
        {
            WriteFailure("Could not read mapped view capacity.");
            return false;
        }

        var delta = capacity.Value - structSize;

        Console.WriteLine($"  Marshal.SizeOf<Shared>() : {structSize:N0} bytes");
        Console.WriteLine($"  Mapped view capacity     : {capacity.Value:N0} bytes");
        Console.WriteLine($"  Delta                    : {delta:N0} bytes");
        Console.WriteLine($"  (capacity is page-rounded to {PageSize:N0}-byte boundaries)");

        if (structSize > capacity.Value)
        {
            WriteFailure($"  MISMATCH - local struct is LARGER than the region by {-delta:N0} bytes.");
            Console.WriteLine("  Reading it would run past the end of the mapping.");
            return false;
        }

        if (delta >= PageSize)
        {
            WriteFailure("  MISMATCH - region is larger than the struct by more than one page.");
            Console.WriteLine("  The game likely added fields your copy does not know about.");
            return false;
        }

        WriteSuccess($"  CONSISTENT - struct fits with {delta:N0} bytes of page padding.");
        return true;
    }

    /// <summary>
    /// Spot-check a few well-known fields for plausible values.
    /// </summary>
    /// <remarks>
    /// A struct can be the right size but still misaligned. If these read as garbage,
    /// the layout has shifted even though the size check passed. Note that -1 is a
    /// legitimate "not currently available" sentinel throughout this API, not an error.
    /// </remarks>
    private static void ReportSanityFields(RaceRoomTelemetryService service)
    {
        Console.WriteLine("--- Part C: field sanity spot-check ---");

        var shared = service.TryReadShared();
        if (shared is null)
        {
            WriteFailure("Could not read the shared struct.");
            return;
        }

        var s = shared.Value;

        Console.WriteLine($"  GameSimulationTime  : {Describe(s.Player.GameSimulationTime)}");
        Console.WriteLine($"  CompletedLaps       : {Describe(s.CompletedLaps)}");
        Console.WriteLine($"  SessionType         : {Describe(s.SessionType)}");
        Console.WriteLine($"  GameInMenus         : {Describe(s.GameInMenus)}");
        Console.WriteLine($"  VehicleInfo.ModelId : {Describe(s.VehicleInfo.ModelId)}");
        Console.WriteLine($"  LayoutId            : {Describe(s.LayoutId)}");
        Console.WriteLine($"  NumCars             : {Describe(s.NumCars)}");
        Console.WriteLine();
        Console.WriteLine("  Values of -1 mean 'not currently available' and are legitimate.");
        Console.WriteLine("  Wildly large or nonsensical values indicate struct misalignment.");
    }

    private static string Describe(int value) =>
        value == -1 ? "-1 (not available)" : value.ToString();

    /// <summary>
    /// Part D: continuously print the fields the session state machine actually decides on.
    /// </summary>
    /// <remarks>
    /// The size and sanity checks only prove the layout is plausible for one instant. The
    /// recording bugs that matter are behavioural: a session that ends as 'Abandoned' means
    /// <c>GameInMenus</c> read as non-zero, or <c>GameSimulationTime</c> went backwards,
    /// while the driver was still on track. Both are only visible over time, so this mode
    /// samples at the same 60 Hz the recorder uses and prints a line whenever one of those
    /// decision inputs changes, leaving a transcript of exactly what the game reported.
    /// </remarks>
    private static void MonitorLive(RaceRoomTelemetryService service)
    {
        Console.WriteLine("--- Part D: live decision-field monitor ---");
        Console.WriteLine("Drive a session. A line is printed only when a decision field changes.");
        Console.WriteLine("Press any key to stop.");
        Console.WriteLine();

        string? previous = null;
        var lastSimTime = double.MinValue;

        while (!Console.KeyAvailable)
        {
            var shared = service.TryReadShared();

            if (shared is null)
            {
                // The region vanishing is itself a decision input: it is what the recorder
                // sees as a disconnect, so it belongs in the transcript.
                if (previous != "<disconnected>")
                {
                    previous = "<disconnected>";
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  shared memory unavailable");
                }

                Thread.Sleep(250);
                continue;
            }

            var s = shared.Value;
            var simTime = s.Player.GameSimulationTime;

            // Mirrors SessionStateMachine's own restart rule so a spurious restart shows up
            // here rather than only as a mangled session in the database.
            var wentBackwards = lastSimTime > double.MinValue && simTime < lastSimTime - 0.5;
            lastSimTime = simTime;

            var line =
                $"menus={s.GameInMenus} paused={s.GamePaused} replay={s.GameInReplay} " +
                $"garage={s.GamePlayerInGarage} type={s.SessionType} phase={s.SessionPhase} " +
                $"car={s.VehicleInfo.ModelId} track={s.LayoutId} laps={s.CompletedLaps} " +
                $"pit={s.InPitlane} valid={s.CurrentLapValid}";

            if (line != previous || wentBackwards)
            {
                previous = line;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  sim={simTime,10:F3}  {line}");

                if (wentBackwards)
                {
                    WriteFailure("    ^ simulation time went BACKWARDS - the recorder reads this as a restart.");
                }
            }

            // 60 Hz, matching SharedMemoryTelemetrySource so the transcript reflects the
            // same sampling the recorder sees.
            Thread.Sleep(16);
        }

        Console.ReadKey(intercept: true);
        Console.WriteLine();
        Console.WriteLine("  menus/paused/replay/garage of 1 while on track explains an 'Abandoned' session.");
        Console.WriteLine("  A -1 in any of them means the field is unavailable, not that it is false.");
    }

    private static string Describe(double value) =>
        value < 0 ? $"{value:F3} (not available)" : value.ToString("F3");

    private static void WriteSuccess(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }

    private static void WriteFailure(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
