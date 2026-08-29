using System.Security.Claims;
using GameTracker.Domain.Dtos;
using GameTracker.Domain.Entities;
using GameTrackerBlazorServerApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerBlazorServerApp.Controllers;

/// <summary>
/// Session headers uploaded by the recording client.
/// </summary>
[ApiController]
[Route("api/sessions")]
[Authorize(Policy = "UserOrAdmin")]
public sealed class SessionsController(ApplicationDbContext context) : ControllerBase
{
    /// <summary>
    /// Creates or updates a session. Keyed on the client-generated GUID, so a retried
    /// upload after a dropped connection updates the row instead of duplicating it.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Upsert(
        [FromBody] SessionUploadDto dto,
        CancellationToken cancellationToken = default)
    {
        if (dto.Id == Guid.Empty)
        {
            return BadRequest("Session id is required.");
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var existing = await context.Sessions
            .FirstOrDefaultAsync(s => s.Id == dto.Id, cancellationToken);

        if (existing is null)
        {
            context.Sessions.Add(new Session
            {
                Id = dto.Id,
                GameId = dto.GameId,
                CarExternalId = dto.CarExternalId,
                TrackExternalId = dto.TrackExternalId,
                SessionType = dto.SessionType,
                StartedAtUtc = dto.StartedAtUtc,
                EndedAtUtc = dto.EndedAtUtc,
                EndReason = dto.EndReason,
                // Stamped from the token, never from the payload: a client must not be
                // able to file a session against someone else's account.
                UserId = userId
            });
        }
        else
        {
            if (existing.UserId != userId)
            {
                // The GUID is client-generated, so a collision (or a forged id) must not
                // let one user overwrite another user's session.
                return Forbid();
            }

            // Only the closing details can change on a re-post; identity fields are fixed
            // at session start.
            existing.EndedAtUtc = dto.EndedAtUtc;
            existing.EndReason = dto.EndReason;
        }

        await context.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
