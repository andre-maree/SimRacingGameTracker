using GameTracker.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerWpfClientApp.Data;

/// <summary>
/// Local SQLite store: the offline catalogue mirror plus everything recorded on this
/// machine before it is uploaded.
/// </summary>
/// <remarks>
/// The client is a cache, not a second source of truth. Catalogue rows are replicas keyed
/// by the server id, and <c>ServerVersion</c> is stored purely so the sync cursor can be
/// recomputed. Tombstones are never persisted here: the sync service deletes the local row
/// instead, so the UI cannot accidentally show a car the server has retired.
/// </remarks>
public sealed class ClientDbContext(DbContextOptions<ClientDbContext> options) : DbContext(options)
{
    public DbSet<Game> Games => Set<Game>();

    public DbSet<Car> Cars => Set<Car>();

    public DbSet<Track> Tracks => Set<Track>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Stint> Stints => Set<Stint>();

    public DbSet<Lap> Laps => Set<Lap>();

    /// <summary>Lap summaries awaiting upload, or already uploaded and kept for local history.</summary>
    public DbSet<TelemetryRecord> LocalTelemetry => Set<TelemetryRecord>();

    public DbSet<LapInputTelemetry> LapInputTelemetry => Set<LapInputTelemetry>();

    public DbSet<SyncMetadata> SyncMetadata => Set<SyncMetadata>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Game>(entity =>
        {
            // Ids mirror the server: they are replicated, never generated locally.
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Ignore(e => e.IsDeleted);
            entity.Ignore(e => e.Cars);
            entity.Ignore(e => e.Tracks);
        });

        builder.Entity<Car>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();

            // No IsDeleted client-side: a tombstone results in a local row deletion, so a
            // retired car can never linger in the offline browser.
            entity.Ignore(e => e.IsDeleted);
            entity.Ignore(e => e.Game);

            entity.HasIndex(e => new { e.GameId, e.ExternalId }).IsUnique();
        });

        builder.Entity<Track>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Ignore(e => e.IsDeleted);
            entity.Ignore(e => e.Game);

            entity.HasIndex(e => new { e.GameId, e.ExternalId }).IsUnique();
        });

        builder.Entity<Session>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasMany(e => e.Stints)
                .WithOne(s => s.Session)
                .HasForeignKey(s => s.SessionId)
                // Local recording data is owned by its session, so cascading is correct
                // here even though the server deliberately restricts it.
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.StartedAtUtc);
        });

        builder.Entity<Stint>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasMany(e => e.Laps)
                .WithOne(l => l.Stint)
                .HasForeignKey(l => l.StintId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Lap>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(e => e.InputTelemetry)
                .WithOne(t => t.Lap)
                .HasForeignKey<LapInputTelemetry>(t => t.LapId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.StintId, e.LapNumber });
        });

        builder.Entity<LapInputTelemetry>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => e.LapId).IsUnique();
        });

        builder.Entity<TelemetryRecord>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => e.SessionId);

            // Upload queue drain order: pending rows are those with a null UploadedAtUtc.
            entity.HasIndex(e => new { e.UploadedAtUtc, e.RecordedAtUtc });
        });

        builder.Entity<SyncMetadata>(entity =>
        {
            // One cursor row per synced collection.
            entity.HasIndex(e => e.EntityName).IsUnique();
        });
    }
}
