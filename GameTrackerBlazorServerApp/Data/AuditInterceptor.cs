using System.Security.Claims;
using System.Text.Json;
using GameTracker.Domain.Entities;
using GameTracker.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GameTrackerBlazorServerApp.Data;

/// <summary>
/// Writes an <see cref="AuditTrail"/> row for every tracked entity change.
/// </summary>
/// <remarks>
/// Application-level auditing captures the acting user, which database triggers cannot
/// see. The triggers added alongside this remain as defence-in-depth for changes that
/// bypass EF entirely. Inserts are audited in <c>SavedChanges</c> because store-generated
/// keys do not exist until after the write.
/// </remarks>
public sealed class AuditInterceptor(IHttpContextAccessor httpContextAccessor) : SaveChangesInterceptor
{
    private readonly List<PendingAudit> _pending = [];

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        FlushAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(eventData.Context, cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _pending.Clear();

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return Task.CompletedTask;
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        _pending.Clear();

        var userId = httpContextAccessor.HttpContext?.User
            .FindFirstValue(ClaimTypes.NameIdentifier);

        var entries = context.ChangeTracker.Entries()
            .Where(e => e.Entity is not AuditTrail)
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in entries)
        {
            // A soft-delete arrives here as Modified with IsDeleted flipped, so report it
            // as a delete rather than an ordinary update.
            var action = entry.State switch
            {
                EntityState.Added => AuditAction.Create,
                EntityState.Deleted => AuditAction.Delete,
                _ when IsSoftDelete(entry) => AuditAction.Delete,
                _ => AuditAction.Update
            };

            _pending.Add(new PendingAudit(
                entry,
                action,
                userId,
                // Inserts have no key until after the write; everything else is captured now.
                PrimaryKey: action == AuditAction.Create ? null : BuildPrimaryKey(entry),
                // Serialized now, while the entry is still tracked: after SaveChanges a
                // deleted entry is detached and its values are no longer readable.
                OldValues: action == AuditAction.Create ? null : Serialize(entry, original: true),
                NewValues: entry.State == EntityState.Deleted ? null : Serialize(entry, original: false)));
        }
    }

    private static bool IsSoftDelete(EntityEntry entry)
        => entry.Entity is ISyncable
           && entry.State == EntityState.Modified
           && entry.Property(nameof(ISyncable.IsDeleted)) is { IsModified: true, CurrentValue: true };

    private async Task FlushAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null || _pending.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;

        foreach (var pending in _pending)
        {
            var entry = pending.Entry;

            context.Set<AuditTrail>().Add(new AuditTrail
            {
                UserId = pending.UserId,
                TableName = entry.Metadata.GetTableName() ?? entry.Metadata.ClrType.Name,
                Action = pending.Action,
                PrimaryKey = pending.PrimaryKey ?? BuildPrimaryKey(entry),
                OldValues = pending.OldValues,
                NewValues = pending.NewValues,
                ChangedAtUtc = now
            });
        }

        _pending.Clear();

        // Persisting the audit rows is itself a SaveChanges; _pending is already empty so
        // this cannot recurse.
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string BuildPrimaryKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return string.Empty;
        }

        var parts = key.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString() ?? string.Empty);
        return string.Join("|", parts);
    }

    private static string Serialize(EntityEntry entry, bool original)
    {
        var values = new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsShadowProperty())
            {
                continue;
            }

            values[property.Metadata.Name] = original ? property.OriginalValue : property.CurrentValue;
        }

        return JsonSerializer.Serialize(values);
    }

    private sealed record PendingAudit(
        EntityEntry Entry,
        AuditAction Action,
        string? UserId,
        string? PrimaryKey,
        string? OldValues,
        string? NewValues);
}
