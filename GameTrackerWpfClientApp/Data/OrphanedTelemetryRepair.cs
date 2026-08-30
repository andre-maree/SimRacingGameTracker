using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GameTrackerWpfClientApp.Data;

/// <summary>
/// Re-attaches upload queue rows that were written against an empty session id.
/// </summary>
/// <remarks>
/// Earlier builds read the session id from the recorder's live state when persisting a
/// lap, but the finishing lap of every session is harvested while that session is being
/// closed, by which point the state had already been cleared. Those laps were queued
/// against <see cref="Guid.Empty"/>, where the sessions grid could not find them and so
/// reported the session as fully uploaded.
/// <para>
/// The rows are recoverable rather than lost: a queue row shares its primary key with the
/// lap it was written for, so the owning session can be read back through the lap's stint.
/// Repairing is worth doing because these rows are still pending uploads — the uploader
/// sends them regardless of session id, but the user has no way to see that they are
/// outstanding.
/// </para>
/// </remarks>
public static class OrphanedTelemetryRepair
{
    /// <summary>Re-links orphaned rows, returning how many were corrected.</summary>
    public static async Task<int> RunAsync(
        ClientDbContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var orphaned = await context.LocalTelemetry
            .Where(r => r.SessionId == Guid.Empty)
            .ToListAsync(cancellationToken);

        if (orphaned.Count == 0)
        {
            return 0;
        }

        var orphanedIds = orphaned.Select(r => r.Id).ToList();

        // The queue row's key is the lap's key, which is what makes the owning session
        // recoverable at all.
        var sessionByLapId = await context.Laps
            .AsNoTracking()
            .Where(l => orphanedIds.Contains(l.Id))
            .Select(l => new { l.Id, SessionId = l.Stint!.SessionId })
            .ToDictionaryAsync(l => l.Id, l => l.SessionId, cancellationToken);

        var repaired = 0;

        foreach (var record in orphaned)
        {
            if (sessionByLapId.TryGetValue(record.Id, out var sessionId))
            {
                record.SessionId = sessionId;
                repaired++;
            }
        }

        if (repaired > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        // Rows whose lap no longer exists are left alone rather than deleted: they are
        // still valid pending uploads, and the server is the better judge of them than a
        // local cleanup that cannot tell a missing lap from a bug.
        var unattributable = orphaned.Count - repaired;

        logger.LogInformation(
            "Repaired {Repaired} telemetry queue row(s) that had no session id; {Unattributable} could not be attributed.",
            repaired,
            unattributable);

        return repaired;
    }
}
