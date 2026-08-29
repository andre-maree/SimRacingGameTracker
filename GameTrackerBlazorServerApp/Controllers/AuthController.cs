using GameTracker.Domain.Dtos;
using GameTrackerBlazorServerApp.Data;
using GameTrackerBlazorServerApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace GameTrackerBlazorServerApp.Controllers;

/// <summary>
/// Token endpoint for non-browser clients.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    JwtTokenService tokenService,
    ILogger<AuthController> logger) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Deliberately identical to the bad-password response: distinguishing them
            // turns this endpoint into an account enumeration oracle.
            logger.LogWarning("Login failed for unknown email.");
            return Unauthorized();
        }

        // lockoutOnFailure keeps Identity's brute-force protection in play for API logins,
        // not just the browser flow.
        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            logger.LogWarning("Login blocked: account locked out.");
            return StatusCode(StatusCodes.Status423Locked);
        }

        if (!result.Succeeded)
        {
            logger.LogWarning("Login failed: invalid credentials.");
            return Unauthorized();
        }

        var response = await tokenService.CreateTokenAsync(user);
        return Ok(response);
    }
}
