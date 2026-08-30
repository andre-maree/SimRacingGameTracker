using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using GameTrackerWpfClientApp.Services.Connectivity;
using Microsoft.Extensions.Logging;

namespace GameTrackerWpfClientApp.Services.Authentication;

/// <summary>
/// Attaches the bearer token to every outbound API call, reacts to rejection, and turns an
/// unreachable server into an ordinary response rather than an exception.
/// </summary>
/// <remarks>
/// Centralised in a handler rather than at each call site: the sync worker, the telemetry
/// uploader and the UI all share one <c>HttpClient</c> pipeline, and a forgotten header at
/// a single call site would produce an intermittent, hard-to-trace 401.
/// <para>
/// The same argument applies to connectivity. This client is offline-first, so every call
/// site would otherwise need its own <c>catch (HttpRequestException)</c>, and the ones that
/// forgot — or that caught only <c>HttpRequestException</c> and not the
/// <c>TaskCanceledException</c> a connect timeout produces — surfaced a normal offline
/// state as an unhandled exception in the UI. Translating here means there is exactly one
/// place that has to be right.
/// </para>
/// </remarks>
public sealed class AuthenticationHandler : DelegatingHandler
{
    /// <summary>
    /// Marks a response this handler synthesised, as opposed to a genuine 503 from the
    /// server. Without it a server shedding load would be misreported as no network.
    /// </summary>
    private static readonly HttpRequestOptionsKey<bool> OfflineKey = new("GameTracker.Offline");

    private readonly AuthenticationState _authenticationState;
    private readonly ConnectivityState _connectivityState;
    private readonly ILogger<AuthenticationHandler> _logger;

    public AuthenticationHandler(
        AuthenticationState authenticationState,
        ConnectivityState connectivityState,
        ILogger<AuthenticationHandler> logger)
    {
        _authenticationState = authenticationState;
        _connectivityState = connectivityState;
        _logger = logger;
    }

    /// <summary>
    /// True when the response represents an unreachable server rather than a reply.
    /// </summary>
    public static bool IsOffline(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.ServiceUnavailable &&
        response.RequestMessage?.Options.TryGetValue(OfflineKey, out var offline) == true &&
        offline;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Known unreachable and still inside the cooldown: fail immediately rather than
        // pay another connect timeout. The UI stays responsive and the log stays readable.
        if (_connectivityState.IsInCooldown)
        {
            return CreateOfflineResponse(request);
        }

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

        HttpResponseMessage response;

        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return HandleTransportFailure(request, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A connect or response timeout arrives as a cancellation whose token is not
            // the caller's. Filtering on the caller's token is what keeps a genuine
            // shutdown or a user-cancelled request propagating as cancellation.
            return HandleTransportFailure(request, ex);
        }

        // Any reply at all, including a 4xx or 5xx, proves the server was reached.
        _connectivityState.ReportOnline();

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

    private HttpResponseMessage HandleTransportFailure(HttpRequestMessage request, Exception exception)
    {
        var wasOnline = _connectivityState.IsOnline;

        _connectivityState.ReportOffline();

        // Logged once on the transition, at Information: for an offline-first client
        // losing the server is expected, and logging every failed poll as an error would
        // bury the faults that actually need attention.
        if (wasOnline)
        {
            _logger.LogInformation(
                exception,
                "The server could not be reached at {Uri}; continuing offline.",
                request.RequestUri);
        }

        return CreateOfflineResponse(request);
    }

    private static HttpResponseMessage CreateOfflineResponse(HttpRequestMessage request)
    {
        // Marked on the request, because that is what the response carries back to the
        // caller via RequestMessage, and it is what lets IsOffline tell this apart from a
        // 503 the server itself chose to send.
        request.Options.Set(OfflineKey, true);

        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            ReasonPhrase = "The GameTracker server could not be reached.",
            RequestMessage = request
        };
    }
}
