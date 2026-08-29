using System.Diagnostics;
using System.Security.Claims;

namespace GameTrackerBlazorServerApp.Middleware;

/// <summary>
/// Wraps each request in a logging scope carrying the request and user identity.
/// </summary>
/// <remarks>
/// The server handles concurrent uploads from several clients, so log lines from different
/// requests interleave. Without a correlation key a failed batch cannot be reconstructed
/// from the surrounding noise, and "which user" is exactly the question asked first when a
/// client reports missing laps.
/// <para>
/// Registered as middleware rather than logged per controller so it covers every endpoint,
/// including ones added later, and cannot be forgotten.
/// </para>
/// </remarks>
public sealed class RequestLoggingScopeMiddleware(RequestDelegate next, ILogger<RequestLoggingScopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // TraceIdentifier is already unique per request and is surfaced in ASP.NET Core
        // error responses, so it links a user-visible failure to these log lines.
        var scopeState = new Dictionary<string, object>
        {
            ["RequestId"] = context.TraceIdentifier,
            ["RequestPath"] = context.Request.Path.Value ?? string.Empty
        };

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is not null)
        {
            // The id, never the email: log files are copied around far more casually than
            // the database, so personal data does not belong in them.
            scopeState["UserId"] = userId;
        }

        using var scope = logger.BeginScope(scopeState);

        // Only API traffic is timed. Blazor's static assets and circuit polling would
        // otherwise bury the handful of lines that actually matter.
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();

            // 5xx at Error, 4xx at Warning, success at Information: the level carries the
            // triage decision, so a log filter alone separates faults from normal traffic.
            var level = context.Response.StatusCode switch
            {
                >= 500 => LogLevel.Error,
                >= 400 => LogLevel.Warning,
                _ => LogLevel.Information
            };

            logger.Log(
                level,
                "{Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
