using System.Security.Claims;
using GameTracker.Domain.Dtos;
using GameTracker.Domain.Entities;
using GameTrackerBlazorServerApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerBlazorServerApp.Controllers;

/// <summary>
/// Batched lap telemetry uploads from the recording client.
/// </summary>
/// <remarks>
/// Uploads happen over an unreliable link and are retried, so the endpoint is idempotent
/// on the client-generated record GUID: re-sending a batch that was already committed is
/// reported as duplicates rather than inserting the laps twice.
/// </remarks>
[ApiController]
[Route("api/telemetry")]
[Authorize(Policy = "UserOrAdmin")]
public sealed class TelemetryController(ApplicationDbContext context) : ControllerBase
{
    private const int MaxBatchSize = 500;

    [HttpPost("batch")]
    [ProducesResponseType<TelemetryBatchResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TelemetryBatchResponse>> UploadBatch(
        [FromBody] TelemetryBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Records.Count == 0)
        {
            return Ok(new TelemetryBatchResponse());
        }

        if (request.Records.Count > MaxBatchSize)
        {
            return BadRequest($"Batch size exceeds the {MaxBatchSize} record limit.");
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        // Deduplicate within the batch first: a client retrying mid-flush can legitimately
        // repeat a record, and EF would otherwise throw on the duplicate key.
        var incoming = request.Records
            .Where(r => r.Id != Guid.Empty)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToList();

        if (incoming.Count != request.Records.Count)
        {
            // An empty GUID means the client never assigned one, which breaks idempotency
            // outright, so reject rather than silently generating ids server-side.
            if (request.Records.Any(r => r.Id == Guid.Empty))
            {
                return BadRequest("Every record requires a client-generated id.");
            }
        }

        var ids = incoming.Select(r => r.Id).ToList();
        var existing = await context.TelemetryRecords
            .AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        var existingSet = existing.ToHashSet();
        var toInsert = incoming.Where(r => !existingSet.Contains(r.Id)).ToList();

        foreach (var record in toInsert)
        {
            context.TelemetryRecords.Add(new TelemetryRecord
            {
                Id = record.Id,
                SessionId = record.SessionId,
                GameId = record.GameId,
                CarExternalId = record.CarExternalId,
                TrackExternalId = record.TrackExternalId,
                LapNumber = record.LapNumber,
                LapTime = record.LapTime,
                IsValid = record.IsValid,
                RecordedAtUtc = record.RecordedAtUtc,
                // Stamped from the token so a client cannot attribute laps to another user.
                UserId = userId
            });
        }

        if (toInsert.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        return Ok(new TelemetryBatchResponse
        {
            Accepted = toInsert.Count,
            Duplicates = incoming.Count - toInsert.Count
        });
    }
}
