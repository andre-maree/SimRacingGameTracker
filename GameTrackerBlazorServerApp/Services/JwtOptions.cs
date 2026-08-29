namespace GameTrackerBlazorServerApp.Services;

/// <summary>
/// JWT signing settings bound from configuration.
/// </summary>
/// <remarks>
/// <see cref="Key"/> is deliberately not present in <c>appsettings.json</c>: it must come
/// from user secrets in development or the environment/key vault in production. The brief
/// forbids plaintext credentials in configuration files, and a leaked signing key lets
/// anyone mint an Admin token.
/// </remarks>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "GameTracker";

    public string Audience { get; set; } = "GameTrackerClients";

    public string Key { get; set; } = string.Empty;

    /// <summary>Token lifetime in hours. 24 balances security against re-login friction.</summary>
    public int ExpiryHours { get; set; } = 24;
}
