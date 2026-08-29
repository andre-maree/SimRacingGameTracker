namespace GameTrackerWpfClientApp.Services;

/// <summary>
/// Names of the configured <see cref="System.Net.Http.HttpClient"/> instances.
/// </summary>
/// <remarks>
/// A constant rather than a literal at each call site: a typo in a client name silently
/// yields a default <c>HttpClient</c> with no base address and, worse, no authentication
/// handler, which would fail as an unexplained 401 at runtime.
/// </remarks>
public static class ApiClientNames
{
    /// <summary>The authenticated client pointed at the GameTracker server.</summary>
    public const string GameTrackerApi = "GameTrackerApi";
}
