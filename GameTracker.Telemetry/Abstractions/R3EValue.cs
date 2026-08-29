namespace GameTracker.Telemetry.Abstractions;

/// <summary>
/// Helpers for R3E's sentinel convention: shared memory reports <c>-1</c> for any value
/// that is not currently available, rather than omitting it. Treating those as real
/// readings is the single easiest way to corrupt a lap record, so every raw value is
/// funnelled through here.
/// </summary>
public static class R3EValue
{
    /// <summary>The sentinel RaceRoom writes when a value is unavailable.</summary>
    public const int Unavailable = -1;

    /// <summary>Float comparisons need a tolerance; the sentinel is exactly -1.0f.</summary>
    private const float Epsilon = 0.0001f;

    public static bool IsAvailable(int value) => value != Unavailable;

    public static bool IsAvailable(float value) => value > Unavailable + Epsilon;

    public static bool IsAvailable(double value) => value > Unavailable + Epsilon;

    /// <summary>Returns the value, or null when RaceRoom reports it as unavailable.</summary>
    public static int? ToNullable(int value) => IsAvailable(value) ? value : null;

    /// <summary>Returns the value as a double, or null when unavailable.</summary>
    public static double? ToNullable(float value) => IsAvailable(value) ? value : null;

    /// <summary>Returns the value, or null when unavailable.</summary>
    public static double? ToNullable(double value) => IsAvailable(value) ? value : null;

    /// <summary>Interprets an R3E 0/1 flag, mapping the -1 sentinel to null.</summary>
    public static bool? ToNullableFlag(int value) => IsAvailable(value) ? value != 0 : null;

    /// <summary>Interprets an R3E 0/1 flag, treating "unavailable" as <paramref name="fallback"/>.</summary>
    public static bool ToFlag(int value, bool fallback = false)
        => IsAvailable(value) ? value != 0 : fallback;
}
