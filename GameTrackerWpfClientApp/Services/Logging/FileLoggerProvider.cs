using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace GameTrackerWpfClientApp.Services.Logging;

/// <summary>
/// A minimal rolling file sink for the desktop client.
/// </summary>
/// <remarks>
/// The desktop app has no console: a crash or a stalled upload on a user's machine leaves
/// no evidence at all unless something writes to disk, so a file sink is the only way to
/// diagnose anything reported from the field.
/// <para>
/// Hand-rolled rather than pulling in Serilog: the requirement here is one rolling text
/// file, and a logging framework plus its sink packages is a large dependency for that.
/// The provider is deliberately small enough to reason about.
/// </para>
/// </remarks>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    /// <summary>Files older than this are deleted on startup.</summary>
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(14);

    /// <summary>
    /// Bound on the pending queue. Logging must never be able to exhaust memory when the
    /// disk stalls, and dropping diagnostics is strictly better than taking down the app
    /// that is producing them.
    /// </summary>
    private const int MaxQueuedMessages = 2048;

    private readonly BlockingCollection<string> _queue = new(MaxQueuedMessages);
    private readonly string _directory;
    private readonly Thread _writer;
    private IExternalScopeProvider? _scopeProvider;
    private bool _disposed;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        DeleteExpiredFiles();

        // A dedicated background thread rather than the thread pool: this runs for the
        // life of the process, and parking a pool thread indefinitely starves everything
        // else. IsBackground keeps it from holding the process open at shutdown.
        _writer = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "FileLogger",

            // Below normal: writing diagnostics must never compete with the 60 Hz
            // telemetry poll loop.
            Priority = ThreadPriority.BelowNormal
        };

        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        // Bounded join: a flush is worth waiting for, but a stuck disk must not prevent
        // the application from closing.
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void Enqueue(string message)
    {
        // TryAdd, never Add: a full queue drops the message instead of blocking the caller,
        // which could be the recording consumer task.
        _queue.TryAdd(message);
    }

    private void WriteLoop()
    {
        foreach (var message in _queue.GetConsumingEnumerable())
        {
            try
            {
                // Reopened per batch rather than holding a handle: a daily roll then needs
                // no special case, and the file stays readable while the app is running.
                File.AppendAllText(CurrentFilePath(), message, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Swallowed deliberately. A logger that throws would turn a disk problem
                // into an application failure, and there is nowhere left to report it.
            }
        }
    }

    /// <summary>Rolls daily by date-stamped filename, which needs no rename or lock.</summary>
    private string CurrentFilePath() =>
        Path.Combine(_directory, $"gametracker-{DateTime.Now:yyyy-MM-dd}.log");

    private void DeleteExpiredFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - RetentionPeriod;

            foreach (var file in Directory.EnumerateFiles(_directory, "gametracker-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // Housekeeping is best-effort: a locked or unreadable old file must not stop
            // the application from starting.
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            provider._scopeProvider?.Push(state);

        // Level filtering is the framework's job via configuration; answering true here
        // keeps this provider from silently overriding appsettings.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder();

            // Sortable timestamp with offset: log files get compared against game clocks
            // and server logs across time zones.
            builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(Level(logLevel)).Append("] ")
                .Append(category);

            AppendScopes(builder);

            builder.Append(" - ").Append(formatter(state, exception));

            if (exception is not null)
            {
                // Full ToString, not just the message: the stack trace is the entire
                // reason a field report is actionable.
                builder.AppendLine().Append(exception);
            }

            builder.AppendLine();
            provider.Enqueue(builder.ToString());
        }

        /// <summary>
        /// Appends the active scopes, which is what makes a flat file navigable: without
        /// them a lap-upload failure cannot be tied back to the session it belongs to.
        /// </summary>
        private void AppendScopes(StringBuilder builder)
        {
            provider._scopeProvider?.ForEachScope(
                (scope, target) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object>> values)
                    {
                        target.Append(" {")
                            .Append(string.Join(", ", values.Select(v => $"{v.Key}={v.Value}")))
                            .Append('}');
                    }
                    else if (scope is not null)
                    {
                        target.Append(" {").Append(scope).Append('}');
                    }
                },
                builder);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }
}
