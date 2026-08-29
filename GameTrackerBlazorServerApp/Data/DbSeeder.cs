using System.Text.Json;
using System.Text.Json.Serialization;
using GameTracker.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerBlazorServerApp.Data;

/// <summary>
/// Applies migrations and seeds roles, the default administrator and the RaceRoom
/// catalogue parsed from <c>r3e-data.json</c>.
/// </summary>
/// <remarks>
/// Seeding is idempotent: it upserts on the natural key <c>(GameId, ExternalId)</c> and
/// only writes rows whose values actually changed, so a re-run does not churn
/// <c>ServerVersion</c> and force every client into a needless full resync.
/// </remarks>
public static class DbSeeder
{
    public const string AdminRole = "Admin";
    public const string UserRole = "User";
    private const string R3EShortName = "R3E";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var context = provider.GetRequiredService<ApplicationDbContext>();
        await context.Database.MigrateAsync(cancellationToken);

        await SeedRolesAsync(provider);
        await SeedAdminAsync(provider, provider.GetRequiredService<IConfiguration>());
        await SeedCatalogueAsync(context, provider.GetRequiredService<IWebHostEnvironment>(), cancellationToken);
    }

    private static async Task SeedRolesAsync(IServiceProvider provider)
    {
        var roleManager = provider.GetRequiredService<RoleManager<IdentityRole>>();

        foreach (var role in new[] { AdminRole, UserRole })
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }
    }

    private static async Task SeedAdminAsync(IServiceProvider provider, IConfiguration configuration)
    {
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = configuration["Seed:AdminEmail"] ?? "admin@gametracker.local";

        // Never hard-code a credential: the password comes from user secrets or the
        // environment. The brief explicitly forbids plaintext credentials in appsettings.
        var password = configuration["Seed:AdminPassword"];

        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            if (!await userManager.IsInRoleAsync(existing, AdminRole))
            {
                await userManager.AddToRoleAsync(existing, AdminRole);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            // No password configured: skip rather than invent a guessable default.
            return;
        }

        var admin = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(admin, password);
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(admin, AdminRole);
        }
    }

    private static async Task SeedCatalogueAsync(
        ApplicationDbContext context,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(environment.ContentRootPath, "Data", "r3e-data.json");
        if (!File.Exists(path))
        {
            return;
        }

        var game = await context.Games.FirstOrDefaultAsync(g => g.ShortName == R3EShortName, cancellationToken);
        if (game is null)
        {
            game = new Game { Name = "RaceRoom Racing Experience", ShortName = R3EShortName };
            context.Games.Add(game);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var stream = File.OpenRead(path);
        var data = await JsonSerializer.DeserializeAsync<R3EData>(stream, JsonOptions, cancellationToken);
        if (data is null)
        {
            return;
        }

        SeedCars(context, game.Id, data);
        SeedTracks(context, game.Id, data);

        await context.SaveChangesAsync(cancellationToken);
    }

    private static void SeedCars(ApplicationDbContext context, int gameId, R3EData data)
    {
        var classNames = data.Classes.Values.ToDictionary(c => c.Id, c => c.Name);
        var existing = context.Cars.Where(c => c.GameId == gameId).ToDictionary(c => c.ExternalId);

        foreach (var source in data.Cars.Values)
        {
            var className = classNames.GetValueOrDefault(source.Class);

            if (existing.TryGetValue(source.Id, out var car))
            {
                // Only touch rows that actually differ, otherwise every seed run would
                // bump ServerVersion and trigger a full client resync for no reason.
                if (car.Name == source.Name && car.Manufacturer == source.BrandName
                    && car.Class == className && !car.IsDeleted)
                {
                    continue;
                }

                car.Name = source.Name;
                car.Manufacturer = source.BrandName;
                car.Class = className;
                car.IsDeleted = false;
                continue;
            }

            context.Cars.Add(new Car
            {
                GameId = gameId,
                ExternalId = source.Id,
                Name = source.Name,
                Manufacturer = source.BrandName,
                Class = className
            });
        }
    }

    private static void SeedTracks(ApplicationDbContext context, int gameId, R3EData data)
    {
        var existing = context.Tracks.Where(t => t.GameId == gameId).ToDictionary(t => t.ExternalId);

        foreach (var track in data.Tracks.Values)
        {
            foreach (var layout in track.Layouts)
            {
                // One row per layout: shared memory reports LayoutId, not the track id,
                // so the layout is what telemetry can actually join on.
                if (existing.TryGetValue(layout.Id, out var stored))
                {
                    if (stored.Name == track.Name && stored.LayoutName == layout.Name && !stored.IsDeleted)
                    {
                        continue;
                    }

                    stored.Name = track.Name;
                    stored.LayoutName = layout.Name;
                    stored.IsDeleted = false;
                    continue;
                }

                context.Tracks.Add(new Track
                {
                    GameId = gameId,
                    ExternalId = layout.Id,
                    Name = track.Name,
                    LayoutName = layout.Name
                });
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private sealed class R3EData
    {
        [JsonPropertyName("cars")]
        public Dictionary<string, R3ECar> Cars { get; set; } = [];

        [JsonPropertyName("tracks")]
        public Dictionary<string, R3ETrack> Tracks { get; set; } = [];

        [JsonPropertyName("classes")]
        public Dictionary<string, R3EClass> Classes { get; set; } = [];
    }

    private sealed class R3ECar
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? BrandName { get; set; }
        public int Class { get; set; }
    }

    private sealed class R3EClass
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class R3ETrack
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("layouts")]
        public List<R3ELayout> Layouts { get; set; } = [];
    }

    private sealed class R3ELayout
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
