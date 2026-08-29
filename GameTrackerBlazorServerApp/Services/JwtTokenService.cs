using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GameTracker.Domain.Dtos;
using GameTrackerBlazorServerApp.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GameTrackerBlazorServerApp.Services;

/// <summary>
/// Issues signed JWTs for API clients that cannot use cookies (the WPF app).
/// </summary>
public sealed class JwtTokenService(
    UserManager<ApplicationUser> userManager,
    IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public async Task<LoginResponse> CreateTokenAsync(ApplicationUser user)
    {
        var roles = await userManager.GetRolesAsync(user);

        var claims = new List<Claim>
        {
            // NameIdentifier is what the audit interceptor and telemetry upload read to
            // stamp the acting user, so it must be the stable Id, not the email.
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        // Role claims are embedded so [Authorize(Roles = ...)] works without a database
        // hit per request. The 24-hour expiry bounds how stale a revoked role can be.
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var expires = DateTime.UtcNow.AddHours(_options.ExpiryHours);
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires,
            signingCredentials: credentials);

        return new LoginResponse
        {
            AccessToken = new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresAtUtc = expires,
            Roles = [.. roles]
        };
    }
}
