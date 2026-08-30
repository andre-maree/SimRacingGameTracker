namespace GameTrackerWpfClientApp.Services.Authentication;

/// <summary>
/// The application's current sign-in state, shared between the HTTP layer and the UI.
/// </summary>
/// <remarks>
/// Registered as a singleton so that background workers (sync, telemetry upload) and the
/// Blazor components observe the same state. The handler raises
/// <see cref="SignedOut"/> instead of navigating directly, because a
/// <c>DelegatingHandler</c> runs on a background thread and must not touch the UI.
/// </remarks>
public sealed class AuthenticationState
{
    private readonly ITokenStore _tokenStore;
    private StoredToken? _token;

    public AuthenticationState(ITokenStore tokenStore) => _tokenStore = tokenStore;

    /// <summary>Raised when the token is cleared, whether by sign-out or by a server 401.</summary>
    public event Action? SignedOut;

    /// <summary>Raised when a token is stored, so the shell can leave the login screen.</summary>
    public event Action? SignedIn;

    public bool IsAuthenticated => _token is { IsExpired: false };

    public IReadOnlyList<string> Roles => _token?.Roles ?? [];

    public bool IsAdmin => Roles.Contains("Admin");

    /// <summary>
    /// Loads any previously persisted token at startup so a returning user is not asked
    /// to sign in again while their token is still valid.
    /// </summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _tokenStore.LoadAsync(cancellationToken);

        if (stored is null)
        {
            return;
        }

        if (stored.IsExpired)
        {
            // Do not keep a token we already know the server will reject.
            await _tokenStore.ClearAsync(cancellationToken);

            // Announced, not just discarded: an expired token is indistinguishable from a
            // sign-out as far as the rest of the application is concerned, and staying
            // silent here leaves the shell showing a signed-in chrome that no request can
            // actually satisfy.
            SignedOut?.Invoke();
            return;
        }

        _token = stored;
        SignedIn?.Invoke();
    }

    /// <summary>Returns the token to attach, or <c>null</c> when there is nothing usable.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_token is null)
        {
            return null;
        }

        if (_token.IsExpired)
        {
            // Expired locally: clear now rather than sending a request that is certain
            // to come back 401.
            await SignOutAsync(cancellationToken);
            return null;
        }

        return _token.AccessToken;
    }

    public async Task SignInAsync(StoredToken token, CancellationToken cancellationToken = default)
    {
        _token = token;
        await _tokenStore.SaveAsync(token, cancellationToken);
        SignedIn?.Invoke();
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _token = null;
        await _tokenStore.ClearAsync(cancellationToken);
        SignedOut?.Invoke();
    }
}
