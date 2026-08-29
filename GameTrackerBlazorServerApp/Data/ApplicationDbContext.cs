using GameTracker.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerBlazorServerApp.Data
{
    public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
    {
        public DbSet<Game> Games => Set<Game>();

        public DbSet<Car> Cars => Set<Car>();

        public DbSet<Track> Tracks => Set<Track>();

        public DbSet<TelemetryRecord> TelemetryRecords => Set<TelemetryRecord>();

        public DbSet<Session> Sessions => Set<Session>();

        public DbSet<AuditTrail> AuditTrails => Set<AuditTrail>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Monotonic counter backing every ISyncable.ServerVersion. A sequence (not
            // ROWVERSION) because ROWVERSION is per-database and restarts unpredictably.
            builder.HasSequence<long>(ServerVersionInterceptor.SequenceName).StartsAt(1).IncrementsBy(1);

            builder.Entity<Game>(entity =>
            {
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.ShortName).HasMaxLength(50).IsRequired();

                // Sync cursor lookup: /api/sync/changes?since=X filters and orders on this.
                entity.HasIndex(e => e.ServerVersion);
            });

            builder.Entity<Car>(entity =>
            {
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Manufacturer).HasMaxLength(200);
                entity.Property(e => e.Class).HasMaxLength(100);

                // Declaring the trigger is mandatory, not cosmetic: the SQL Server provider
                // writes via an OUTPUT clause, which SQL Server rejects on triggered tables.
                entity.ToTable(tb => tb.HasTrigger("TR_Cars_Audit"));

                // ExternalId (R3E ModelId) is unique within a game, not globally.
                entity.HasIndex(e => new { e.GameId, e.ExternalId }).IsUnique();
                entity.HasIndex(e => e.ServerVersion);

                entity.HasOne(e => e.Game)
                    .WithMany(g => g.Cars)
                    .HasForeignKey(e => e.GameId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<Track>(entity =>
            {
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.LayoutName).HasMaxLength(200);
                entity.Property(e => e.Country).HasMaxLength(100);

                entity.ToTable(tb => tb.HasTrigger("TR_Tracks_Audit"));

                // ExternalId (R3E LayoutId) is unique within a game, not globally.
                entity.HasIndex(e => new { e.GameId, e.ExternalId }).IsUnique();
                entity.HasIndex(e => e.ServerVersion);

                entity.HasOne(e => e.Game)
                    .WithMany(g => g.Tracks)
                    .HasForeignKey(e => e.GameId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<TelemetryRecord>(entity =>
            {
                // Client-generated GUID key: re-uploading a batch is idempotent.
                entity.Property(e => e.Id).ValueGeneratedNever();
                entity.Property(e => e.UserId).HasMaxLength(450);

                // Client-only upload marker; meaningless once the row is on the server.
                entity.Ignore(e => e.UploadedAtUtc);

                entity.HasIndex(e => new { e.GameId, e.CarExternalId });
                entity.HasIndex(e => new { e.GameId, e.TrackExternalId });
                entity.HasIndex(e => e.SessionId);
                entity.HasIndex(e => e.UserId);
            });

            builder.Entity<Session>(entity =>
            {
                // Client-generated GUID: re-posting a session header is idempotent.
                entity.Property(e => e.Id).ValueGeneratedNever();
                entity.Property(e => e.UserId).HasMaxLength(450);

                // Stints/Laps live only in the client SQLite store; the server keeps the
                // session header plus the per-lap TelemetryRecords.
                entity.Ignore(e => e.Stints);

                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => new { e.GameId, e.TrackExternalId });
            });

            builder.Entity<AuditTrail>(entity =>
            {
                entity.Property(e => e.TableName).HasMaxLength(200).IsRequired();
                entity.Property(e => e.PrimaryKey).HasMaxLength(200).IsRequired();
                entity.Property(e => e.UserId).HasMaxLength(450);

                // JSON payloads: schema-flexible, queryable via OPENJSON if ever needed.
                entity.Property(e => e.OldValues).HasColumnType("nvarchar(max)");
                entity.Property(e => e.NewValues).HasColumnType("nvarchar(max)");

                entity.HasIndex(e => new { e.TableName, e.PrimaryKey });
                entity.HasIndex(e => e.ChangedAtUtc);
                entity.HasIndex(e => e.UserId);
            });
        }
    }
}
