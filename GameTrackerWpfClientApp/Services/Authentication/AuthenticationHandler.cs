using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace GameTrackerWpfClientApp.Services.Authentication;

/// <summary>
/// Attaches the bearer token to every outbound API call and reacts to rejection.
/// </summary>
/// <remarks>
/// Centralised in a handler rather than at each call site: the sync worker, the telemetry
/// uploader and the UI all share one <c>HttpClient</c> pipeline, and a forgotten header at
/// a single call site would produce an intermittent, hard-to-trace 401.
/// </remarks>
public sealed class AuthenticationHandler : DelegatingHandler
{
    private readonly AuthenticationState _authenticationState;
    private readonly ILogger<AuthenticationHandler> _logger;

    public AuthenticationHandler(AuthenticationState authenticationState, ILogger<AuthenticationHandler> logger)
    {
        _authenticationState = authenticationState;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // The login call itself must go out unauthenticated, and an existing header set
        // deliberately by a caller is left alone.
        if (request.Headers.Authorization is null)
        {
            var accessToken = await _authenticationState.GetAccessTokenAsync(cancellationToken);

            if (accessToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The server has the final say: a token can be revoked, or the signing key
            // rotated, long before its stated expiry. Clearing here stops background
            // workers from retrying a credential that will never succeed, and the
            // SignedOut event moves the UI to the login screen.
            _logger.LogInformation(
                "Received 401 from {Uri}; clearing the stored token and signing out.",
                request.RequestUri);

            await _authenticationState.SignOutAsync(cancellationToken);
        }

        return response;
    }
}
