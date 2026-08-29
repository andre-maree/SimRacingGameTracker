using System.ComponentModel.DataAnnotations;

namespace GameTracker.Domain.Dtos;

/// <summary>Credentials posted to <c>POST /api/auth/login</c>.</summary>
public class LoginRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

/// <summary>Issued JWT and its expiry. Stored client-side via DPAPI, never in appsettings.</summary>
public class LoginResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public IReadOnlyList<string> Roles { get; set; } = [];
}
