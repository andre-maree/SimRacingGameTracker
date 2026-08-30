using System;

namespace GameTrackerWpfClientApp.Services.Connectivity;

/// <summary>
/// Whether the server is currently believed to be reachable, shared between the HTTP
/// pipeline, the background workers and the UI.
/// </summary>
/// <remarks>
/// The client is offline-first, so "unreachable" is an ordinary operating mode rather than
/// an error. Recording that centrally serves two purposes: the UI can say so plainly, and
/// the HTTP handler can stop firing requests that are certain to fail for a short cooldown
/// after each failure.
/// <para>
/// A cooldown rather than a circuit breaker with a failure count: a single connect failure
/// against a fixed base address is already conclusive, and counting to a threshold would
/// only mean paying several connect timeouts before admitting what the first one proved.
/// </para>
/// </remarks>
public sealed class ConnectivityState
{
    /// <summary>
    /// How long to stop attempting requests after a transport failure.
    /// </summary>
    /// <remarks>
    /// Short on purpose. This window is the worst-case delay before the application
    /// notices the server has come back, so it is traded against the cost of a connect
    /// timeout, not set to the longest interval a user would tolerate.
    /// </remarks>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();

    private bool _isOnline = true;
    private DateTimeOffset? _offlineUntilUtc;

    /// <summary>Raised whenever the reachable/unreachable state flips.</summary>
    /// <remarks>
    /// Raised outside the lock and from whichever thread made the request, so UI
    /// subscribers must marshal to the dispatcher themselves.
    /// </remarks>
    public event Action? Changed;

    /// <summary>
    /// Whether the last attempt reached the server. Optimistic at startup: assuming
    /// offline would suppress the very first request that would prove otherwise.
    /// </summary>
    public bool IsOnline
    {
        get
        {
            lock (_gate)
            {
                return _isOnline;
            }
        }
    }

    /// <summary>
    /// True while inside the cooldown that follows a failure, so callers can be turned
    /// away without paying another connect timeout.
    /// </summary>
    public bool IsInCooldown
    {
        get
        {
            lock (_gate)
            {
                return _offlineUntilUtc is { } until && DateTimeOffset.UtcNow < until;
            }
        }
    }

    /// <summary>Records that a request reached the server, ending any cooldown.</summary>
    public void ReportOnline()
    {
        bool changed;

        lock (_gate)
        {
            changed = !_isOnline;
            _isOnline = true;
            _offlineUntilUtc = null;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Records a transport failure and opens a fresh cooldown window.</summary>
    public void ReportOffline()
    {
        bool changed;

        lock (_gate)
        {
            changed = _isOnline;
            _isOnline = false;

            // Reset on every failure rather than extended only once: a request that
            // slipped through and failed again is evidence the server is still down.
            _offlineUntilUtc = DateTimeOffset.UtcNow.Add(Cooldown);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Clears the cooldown so the next request is attempted immediately, without
    /// claiming the server is reachable.
    /// </summary>
    /// <remarks>
    /// For explicitly user-initiated actions such as signing in or pressing the sync
    /// button: the user pressing a button is a better reason to retry than any timer,
    /// and refusing them because a background poll failed a moment ago would look broken.
    /// </remarks>
    public void RequestProbe()
    {
        lock (_gate)
        {
            _offlineUntilUtc = null;
        }
    }
}
