using System.Net.Http;
using System.Net.Http.Json;
using GameTracker.Domain.Dtos;

namespace GameTrackerWpfClientApp.Services.Authentication;

/// <summary>
/// Talks to the server's authentication endpoint and updates the shared sign-in state.
/// </summary>
public sealed class AuthenticationService
{
    private readonly HttpClient _httpClient;
    private readonly AuthenticationState _authenticationState;

    public AuthenticationService(HttpClient httpClient, AuthenticationState authenticationState)
    {
        _httpClient = httpClient;
        _authenticationState = authenticationState;
    }

    /// <summary>
    /// Attempts a sign-in. Returns <c>true</c> on success, having persisted the token.
    /// </summary>
    public async Task<bool> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        var request = new LoginRequest { Email = email, Password = password };

        var response = await _httpClient.PostAsJsonAsync("api/auth/login", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // The server intentionally returns an undifferentiated 401 for both a bad
            // password and an unknown account, so there is nothing more specific to report.
            return false;
        }

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);

        if (login is null || string.IsNullOrWhiteSpace(login.AccessToken))
        {
            return false;
        }

        await _authenticationState.SignInAsync(
            new StoredToken(login.AccessToken, login.ExpiresAtUtc, login.Roles),
            cancellationToken);

        return true;
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default) =>
        _authenticationState.SignOutAsync(cancellationToken);
}
