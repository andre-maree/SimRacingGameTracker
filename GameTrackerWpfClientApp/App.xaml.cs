using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GameTrackerWpfClientApp.Data;
using GameTracker.Telemetry.Abstractions;
using GameTracker.Telemetry.R3E;
using GameTrackerRazorLibrary.Catalogue;
using GameTrackerWpfClientApp.Services;
using GameTrackerWpfClientApp.Services.Authentication;
using GameTrackerWpfClientApp.Services.Catalogue;
using GameTrackerWpfClientApp.Services.Connectivity;
using GameTrackerWpfClientApp.Services.Logging;
using GameTrackerWpfClientApp.Services.Recording;
using GameTrackerWpfClientApp.Services.Sync;
using GameTrackerWpfClientApp.Services.Upload;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Radzen;

namespace GameTrackerWpfClientApp
{
    public partial class App : Application
    {
        private IHost? _host;

        // Machine-wide names: the guard has to hold across sessions, because two logged-on
        // users polling the same shared-memory telemetry block would record duplicate laps.
        private const string SingleInstanceMutexName = @"Global\GameTracker.DesktopClient.SingleInstance";
        private const string SingleInstanceSignalName = @"Global\GameTracker.DesktopClient.Activate";

        private Mutex? _singleInstanceMutex;
        private EventWaitHandle? _activationSignal;
        private CancellationTokenSource? _activationListenerCts;

        /// <summary>
        /// The application container. Exposed because <c>BlazorWebView.Services</c> is set
        /// from XAML code-behind, which sits outside the DI graph.
        /// </summary>
        public static IServiceProvider ServiceProvider { get; private set; } = default!;

        /// <summary>
        /// Per-user writable location for the local database and the protected token.
        /// </summary>
        public static string LocalDataPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameTracker");

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Claimed before any other work: the database, the log file and the telemetry
            // shared-memory reader are all single-writer resources, so a second process
            // must never get as far as opening them.
            if (!TryClaimSingleInstance())
            {
                SignalRunningInstance();
                Shutdown();
                return;
            }

            StartActivationListener();

            Directory.CreateDirectory(LocalDataPath);

            // HostApplicationBuilder rather than a bare ServiceCollection: it brings
            // configuration, logging and hosted-service lifetimes, which the telemetry
            // poller and the sync worker need in later steps.
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWpfBlazorWebView();
#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
#endif

            // Logs live beside the database, under %LOCALAPPDATA%: a user can be asked for
            // one file from one folder, and Program Files is not writable anyway.
            builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(LocalDataPath, "logs")));

            // Scopes are off by default on most providers; without them a lap-upload
            // failure in a flat file cannot be tied back to the session it came from.
            builder.Logging.Configure(options =>
                options.ActivityTrackingOptions = ActivityTrackingOptions.SpanId | ActivityTrackingOptions.TraceId);

            ConfigureServices(builder.Services, builder.Configuration);

            _host = builder.Build();
            ServiceProvider = _host.Services;

            var logger = _host.Services.GetRequiredService<ILogger<App>>();

            // Attached only once the logger exists, so a crash has somewhere to be written.
            // WPF otherwise terminates a background-thread fault with no record at all.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception; the application is terminating.");

            DispatcherUnhandledException += (_, args) =>
                logger.LogError(args.Exception, "Unhandled dispatcher exception.");

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                // Observed and logged rather than left to the finalizer: an unobserved
                // fault in a background worker is exactly the failure that goes unnoticed.
                logger.LogError(args.Exception, "Unobserved task exception.");
                args.SetObserved();
            };

            logger.LogInformation("GameTracker desktop client starting. Data path: {LocalDataPath}", LocalDataPath);

            // Bring the local store up to date before any component can query it.
            using (var scope = _host.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ClientDbContext>();
                await context.Database.MigrateAsync();

                // Runs after the migration and before the UI: a database written by an
                // earlier build can hold laps queued against no session at all, which the
                // sessions grid reads as "uploaded". Cheap when there is nothing to fix,
                // since the scan is a single indexed predicate.
                await OrphanedTelemetryRepair.RunAsync(context, logger);
            }

            // Restore a previously persisted token before the UI renders, so a returning
            // user is not briefly shown the login screen only to be signed straight back in.
            await _host.Services.GetRequiredService<AuthenticationState>().InitialiseAsync();

            await _host.StartAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            _activationListenerCts?.Cancel();
            _activationListenerCts?.Dispose();
            _activationSignal?.Dispose();

            if (_singleInstanceMutex is not null)
            {
                // Released explicitly rather than left to process teardown, so a crash-free
                // exit never leaves the next launch waiting on an abandoned handle.
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not the owning thread; the handle is dropped by Dispose anyway.
                }

                _singleInstanceMutex.Dispose();
            }

            if (_host is not null)
            {
                // Give hosted services a chance to flush: unsaved recording state is worth
                // more than a fast exit.
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }

            base.OnExit(e);
        }

        /// <summary>
        /// Takes ownership of the machine-wide mutex. Returns false when another instance
        /// already holds it.
        /// </summary>
        private bool TryClaimSingleInstance()
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);

            try
            {
                // Zero timeout: this is a test for ownership, not a queue to join.
                return _singleInstanceMutex.WaitOne(TimeSpan.Zero, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                // The previous instance died without releasing. Ownership has transferred
                // to this wait, so this process is now the single instance.
                return true;
            }
        }

        /// <summary>
        /// Asks the instance that is already running to surface its window, so a second
        /// launch behaves like clicking the taskbar rather than doing nothing at all.
        /// </summary>
        private static void SignalRunningInstance()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(SingleInstanceSignalName, out var signal))
                {
                    using (signal)
                    {
                        signal.Set();
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The running instance belongs to another user account; nothing can be
                // shown to this one, and exiting quietly is still the correct outcome.
            }
        }

        /// <summary>
        /// Watches for a second launch and brings the existing window to the foreground.
        /// </summary>
        private void StartActivationListener()
        {
            _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceSignalName);
            _activationListenerCts = new CancellationTokenSource();

            var signal = _activationSignal;
            var token = _activationListenerCts.Token;

            // A dedicated background thread rather than a task: this blocks for the whole
            // lifetime of the process and has no business occupying a thread-pool slot.
            var listener = new Thread(() =>
            {
                var handles = new[] { signal, token.WaitHandle };

                while (WaitHandle.WaitAny(handles) == 0)
                {
                    Dispatcher.BeginInvoke(new Action(ActivateMainWindow));
                }
            })
            {
                IsBackground = true,
                Name = "SingleInstanceActivationListener"
            };

            listener.Start();
        }

        private void ActivateMainWindow()
        {
            if (MainWindow is null)
            {
                return;
            }

            if (MainWindow.WindowState == WindowState.Minimized)
            {
                MainWindow.WindowState = WindowState.Normal;
            }

            MainWindow.Show();
            MainWindow.Activate();
        }

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<MainWindow>();

            // The database lives under %LOCALAPPDATA%, not next to the executable:
            // Program Files is not writable by a standard user, and per-user data should
            // not be shared between Windows accounts.
            var databasePath = Path.Combine(LocalDataPath, "gametracker.db");
            var connectionString = $"Data Source={databasePath}";

            services.AddDbContext<ClientDbContext>(options => options.UseSqlite(connectionString));

            // A factory as well as the scoped context: Blazor components are long-lived and
            // must own a short-lived context per query, otherwise every browsed page stays
            // tracked for the lifetime of the window.
            services.AddDbContextFactory<ClientDbContext>(
                options => options.UseSqlite(connectionString),
                lifetime: ServiceLifetime.Singleton);

            // The grids are shared with the server app; on the client they read the offline
            // mirror, so browsing works with no connectivity at all.
            services.AddScoped<ICatalogueReader, LocalCatalogueReader>();

            // Scoped alongside the catalogue reader: it opens a short-lived context per
            // query from the singleton factory, so it holds no state of its own.
            services.AddScoped<RecordedSessionReader>();

            // The protected token file sits beside the database, in the same per-user location.
            services.AddSingleton<ITokenStore>(sp => new DpapiTokenStore(
                LocalDataPath,
                sp.GetRequiredService<ILogger<DpapiTokenStore>>()));

            // Singleton: background workers and the UI must observe one sign-in state,
            // otherwise a 401 on the sync worker would leave the UI believing it is still
            // signed in.
            services.AddSingleton<AuthenticationState>();

            // Singleton for the same reason as the sign-in state: a failure observed by the
            // upload worker must spare the UI from repeating the same connect timeout.
            services.AddSingleton<ConnectivityState>();

            services.AddTransient<AuthenticationHandler>();

            // Configuration-driven so a test or self-hosted deployment can be pointed
            // elsewhere without a rebuild.
            var serverBaseAddress = configuration["Server:BaseAddress"] ?? "https://localhost:7157/";

            services.AddHttpClient<AuthenticationService>(client =>
                    client.BaseAddress = new Uri(serverBaseAddress))
                .AddHttpMessageHandler<AuthenticationHandler>();

            // A named client for everything else, so background services resolve the same
            // authenticated pipeline without taking a dependency on AuthenticationService.
            services.AddHttpClient(ApiClientNames.GameTrackerApi, client =>
                    client.BaseAddress = new Uri(serverBaseAddress))
                .AddHttpMessageHandler<AuthenticationHandler>();

            // Singleton so the concurrency guard is genuinely application-wide; it opens
            // its own scoped DbContext per batch rather than capturing one.
            services.AddSingleton<CatalogueSyncService>();

            // Singleton and non-lazy: it owns the memory-mapped handle, and a second
            // instance would open a redundant view of the same region.
            services.AddSingleton<SharedMemoryTelemetrySource>();
            services.AddSingleton<ITelemetrySource>(sp => sp.GetRequiredService<SharedMemoryTelemetrySource>());

            // Registered as a singleton *and* as the hosted service instance, so the UI
            // can subscribe to StatusChanged on the very same object the host is running.
            services.AddSingleton<SessionRecorder>();
            services.AddHostedService(sp => sp.GetRequiredService<SessionRecorder>());

            // Same pattern: the UI reads PendingCount off the running instance.
            services.AddSingleton<TelemetryUploadService>();
            services.AddHostedService(sp => sp.GetRequiredService<TelemetryUploadService>());

            // Radzen dialog/notification/tooltip/context-menu services: the desktop
            // equivalent of AddRadzenComponents() on the server.
            services.AddScoped<DialogService>();
            services.AddScoped<NotificationService>();
            services.AddScoped<TooltipService>();
            services.AddScoped<ContextMenuService>();
        }
    }
}
