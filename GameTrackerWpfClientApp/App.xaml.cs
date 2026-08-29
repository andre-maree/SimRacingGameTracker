using System;
using System.IO;
using System.Windows;
using GameTrackerWpfClientApp.Data;
using GameTracker.Telemetry.Abstractions;
using GameTracker.Telemetry.R3E;
using GameTrackerRazorLibrary.Catalogue;
using GameTrackerWpfClientApp.Services;
using GameTrackerWpfClientApp.Services.Authentication;
using GameTrackerWpfClientApp.Services.Catalogue;
using GameTrackerWpfClientApp.Services.Recording;
using GameTrackerWpfClientApp.Services.Sync;
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

            Directory.CreateDirectory(LocalDataPath);

            // HostApplicationBuilder rather than a bare ServiceCollection: it brings
            // configuration, logging and hosted-service lifetimes, which the telemetry
            // poller and the sync worker need in later steps.
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWpfBlazorWebView();
#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
#endif
            ConfigureServices(builder.Services, builder.Configuration);

            _host = builder.Build();
            ServiceProvider = _host.Services;

            // Bring the local store up to date before any component can query it.
            using (var scope = _host.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ClientDbContext>()
                    .Database.MigrateAsync();
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
            if (_host is not null)
            {
                // Give hosted services a chance to flush: unsaved recording state is worth
                // more than a fast exit.
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }

            base.OnExit(e);
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

            // The protected token file sits beside the database, in the same per-user location.
            services.AddSingleton<ITokenStore>(sp => new DpapiTokenStore(
                LocalDataPath,
                sp.GetRequiredService<ILogger<DpapiTokenStore>>()));

            // Singleton: background workers and the UI must observe one sign-in state,
            // otherwise a 401 on the sync worker would leave the UI believing it is still
            // signed in.
            services.AddSingleton<AuthenticationState>();
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

            // Radzen dialog/notification/tooltip/context-menu services: the desktop
            // equivalent of AddRadzenComponents() on the server.
            services.AddScoped<DialogService>();
            services.AddScoped<NotificationService>();
            services.AddScoped<TooltipService>();
            services.AddScoped<ContextMenuService>();
        }
    }
}
