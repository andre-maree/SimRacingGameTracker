using System.Net.Http;
using System.Net.Http.Json;
using GameTracker.Domain.Dtos;
using GameTrackerWpfClientApp.Services.Connectivity;

namespace GameTrackerWpfClientApp.Services.Authentication;

/// <summary>The outcome of a sign-in attempt.</summary>
/// <remarks>
/// Offline is a third outcome rather than a failure: the remedy is entirely different from
/// a mistyped password, and telling a user their credentials were rejected when the server
/// was never contacted sends them to reset a password that was never wrong.
/// </remarks>
public enum SignInOutcome
{
    Succeeded,
    Rejected,
    Offline
}

/// <summary>
/// Talks to the server's authentication endpoint and updates the shared sign-in state.
/// </summary>
public sealed class AuthenticationService
{
    private readonly HttpClient _httpClient;
    private readonly AuthenticationState _authenticationState;
    private readonly ConnectivityState _connectivityState;

    public AuthenticationService(
        HttpClient httpClient,
        AuthenticationState authenticationState,
        ConnectivityState connectivityState)
    {
        _httpClient = httpClient;
        _authenticationState = authenticationState;
        _connectivityState = connectivityState;
    }

    /// <summary>
    /// Attempts a sign-in, persisting the token on success.
    /// </summary>
    public async Task<SignInOutcome> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        // The user pressing "Sign in" is a better reason to retry than any timer, so an
        // open cooldown from a failed background poll is cleared rather than obeyed.
        _connectivityState.RequestProbe();

        var request = new LoginRequest { Email = email, Password = password };

        var response = await _httpClient.PostAsJsonAsync("api/auth/login", request, cancellationToken);

        if (AuthenticationHandler.IsOffline(response))
        {
            return SignInOutcome.Offline;
        }

        if (!response.IsSuccessStatusCode)
        {
            // The server intentionally returns an undifferentiated 401 for both a bad
            // password and an unknown account, so there is nothing more specific to report.
            return SignInOutcome.Rejected;
        }

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);

        if (login is null || string.IsNullOrWhiteSpace(login.AccessToken))
        {
            return SignInOutcome.Rejected;
        }

        await _authenticationState.SignInAsync(
            new StoredToken(login.AccessToken, login.ExpiresAtUtc, login.Roles),
            cancellationToken);

        return SignInOutcome.Succeeded;
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default) =>
        _authenticationState.SignOutAsync(cancellationToken);
}
