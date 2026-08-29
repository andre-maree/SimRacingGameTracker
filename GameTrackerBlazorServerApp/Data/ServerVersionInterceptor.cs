using GameTracker.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GameTrackerBlazorServerApp.Data;

/// <summary>
/// Stamps every inserted, updated or soft-deleted <see cref="ISyncable"/> row with the
/// next value from the <c>ServerVersionSequence</c> SQL sequence.
/// </summary>
/// <remarks>
/// A server-issued monotonic counter is the only ordering clients can trust. Values are
/// allocated in a single round trip per SaveChanges rather than one query per row, so a
/// bulk catalogue import does not turn into N round trips. Note that gaps are expected:
/// sequences do not roll back with a failed transaction, which is harmless because the
/// sync cursor only cares about ordering, not contiguity.
/// </remarks>
public sealed class ServerVersionInterceptor : SaveChangesInterceptor
{
    public const string SequenceName = "ServerVersionSequence";

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
        {
            StampAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        }

        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            await StampAsync(eventData.Context, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static async Task StampAsync(DbContext context, CancellationToken cancellationToken)
    {
        var entries = context.ChangeTracker
            .Entries<ISyncable>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (entries.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var versions = await GetNextVersionsAsync(context, entries.Count, cancellationToken);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            if (entry.State == EntityState.Deleted)
            {
                // Never hard-delete a synced row: clients would keep a stale copy forever
                // because nothing would ever tell them it is gone. Convert to a tombstone.
                entry.State = EntityState.Modified;
                entry.Entity.IsDeleted = true;
            }

            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
            }
            else
            {
                entry.Property(nameof(ISyncable.CreatedAtUtc)).IsModified = false;
            }

            entry.Entity.ModifiedAtUtc = now;
            entry.Entity.ServerVersion = versions[i];
        }
    }

    private static async Task<IReadOnlyList<long>> GetNextVersionsAsync(
        DbContext context,
        int count,
        CancellationToken cancellationToken)
    {
        // A single NEXT VALUE FOR projected over a generated row set: one round trip for
        // the whole batch, and SQL Server still guarantees each row a distinct value.
        var rows = string.Join(",", Enumerable.Range(0, count).Select(i => $"({i})"));
        var sql = $"SELECT NEXT VALUE FOR [{SequenceName}] AS [Value] FROM (VALUES {rows}) AS t(n)";

        var values = await context.Database
            .SqlQueryRaw<long>(sql)
            .ToListAsync(cancellationToken);

        return values;
    }
}
