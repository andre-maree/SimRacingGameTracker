using GameTrackerBlazorServerApp.Components;
using GameTrackerBlazorServerApp.Components.Account;
using GameTrackerBlazorServerApp.Data;
using GameTrackerBlazorServerApp.Middleware;
using GameTrackerBlazorServerApp.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Radzen;
using System.Text;

const string JwtOrCookieScheme = "JwtOrCookie";

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Required by app.MapControllers() for the sync and telemetry Web API endpoints.
builder.Services.AddControllers();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

if (string.IsNullOrWhiteSpace(jwtOptions.Key))
{
    if (!builder.Environment.IsDevelopment())
    {
        // Never fall back to a generated key outside development: tokens would be
        // invalidated on every restart and, worse, the failure would be silent.
        throw new InvalidOperationException("Jwt:Key is not configured. Set it via environment or key vault.");
    }

    // Development convenience only. The key stays out of appsettings.json by design.
    jwtOptions.Key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
    builder.Services.PostConfigure<JwtOptions>(options => options.Key = jwtOptions.Key);
}

builder.Services.AddScoped<JwtTokenService>();

builder.Services.AddAuthentication(options =>
    {
        // A policy scheme, so one app can serve the cookie-authenticated Blazor UI and
        // the bearer-authenticated API without either stepping on the other.
        options.DefaultScheme = JwtOrCookieScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            // The default 5-minute clock skew silently extends every token's life.
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    })
    .AddPolicyScheme(JwtOrCookieScheme, JwtOrCookieScheme, options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            string? authorization = context.Request.Headers.Authorization;

            return authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? JwtBearerDefaults.AuthenticationScheme
                : IdentityConstants.ApplicationScheme;
        };
    })
    // MapIdentityApi signs in with IdentityConstants.BearerScheme, so its handler must
    // stay registered even though our own /api/auth/login issues the JWT the WPF client uses.
    .AddBearerToken(IdentityConstants.BearerScheme)
    // AddIdentityCookies() returns IdentityCookiesBuilder rather than AuthenticationBuilder,
    // so it must come last in this chain.
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ServerVersionInterceptor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
    options.UseSqlServer(connectionString)
           .AddInterceptors(
               sp.GetRequiredService<ServerVersionInterceptor>(),
               sp.GetRequiredService<AuditInterceptor>()));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

// Role policies. Admin is allowed anywhere a User is, so it is listed in both.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"))
    .AddPolicy("UserOrAdmin", policy => policy.RequireRole("User", "Admin"));

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddScoped<CatalogueService>();

// Lets the shared Razor-library grids run against SQL Server here and against the client's
// SQLite mirror in the desktop app, with no change to the components themselves.
builder.Services.AddScoped<GameTrackerRazorLibrary.Catalogue.ICatalogueReader, ServerCatalogueReader>();
builder.Services.AddRadzenComponents();

var app = builder.Build();

// Migrate and seed before serving traffic: roles, the default admin and the R3E catalogue.
await DbSeeder.SeedAsync(app.Services);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// After authentication on purpose: the scope stamps the user id, which does not exist on
// the principal until the authentication middleware has run.
app.UseMiddleware<RequestLoggingScopeMiddleware>();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.MapControllers();

// Login/register/refresh endpoints under /identity, consumed by the WPF client.
app.MapGroup("/identity").MapIdentityApi<ApplicationUser>();

app.Run();
