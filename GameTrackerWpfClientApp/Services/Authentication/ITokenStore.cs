namespace GameTrackerWpfClientApp.Services.Authentication;

/// <summary>
/// The issued access token together with the metadata needed to decide whether it is
/// still usable without a round trip to the server.
/// </summary>
/// <param name="AccessToken">The raw JWT to place in the Authorization header.</param>
/// <param name="ExpiresAtUtc">Server-reported expiry, used for proactive expiry checks.</param>
/// <param name="Roles">Roles carried by the token, so the UI can hide admin-only actions.</param>
public sealed record StoredToken(string AccessToken, DateTime ExpiresAtUtc, IReadOnlyList<string> Roles)
{
    /// <summary>
    /// Treats the token as expired slightly early. Without the skew a token could pass
    /// this check and still be rejected by the server, because the request takes time
    /// to travel and the two clocks are not perfectly aligned.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc - TimeSpan.FromSeconds(30);
}

/// <summary>
/// Persists the access token across application restarts.
/// </summary>
/// <remarks>
/// An interface rather than a concrete class because the DPAPI implementation is
/// Windows-only and untestable on a build agent: tests substitute an in-memory store.
/// </remarks>
public interface ITokenStore
{
    /// <summary>Reads the persisted token, or <c>null</c> when absent or unreadable.</summary>
    Task<StoredToken?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the token, replacing any previously stored one.</summary>
    Task SaveAsync(StoredToken token, CancellationToken cancellationToken = default);

    /// <summary>Removes the persisted token. Safe to call when nothing is stored.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
