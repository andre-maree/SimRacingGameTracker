using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameTrackerBlazorServerApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditTriggers : Migration
    {
        // Defence-in-depth only. The EF AuditInterceptor is the primary audit path because
        // it is the only one that knows the acting user; these triggers exist so a rogue
        // migration, a DBA script or any direct SQL still leaves a trace. Trigger-written
        // rows are stamped with a "sql:" UserId prefix so they are trivially separable from
        // application-written rows.
        private const string CarsTrigger = @"
CREATE OR ALTER TRIGGER [TR_Cars_Audit] ON [Cars]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO [AuditTrails] ([UserId], [TableName], [Action], [PrimaryKey], [OldValues], [NewValues], [ChangedAtUtc])
    SELECT
        'sql:' + ORIGINAL_LOGIN(),
        'Cars',
        CASE WHEN d.[Id] IS NULL THEN 0 WHEN i.[Id] IS NULL THEN 2 ELSE 1 END,
        CAST(COALESCE(i.[Id], d.[Id]) AS nvarchar(200)),
        CASE WHEN d.[Id] IS NULL THEN NULL ELSE (SELECT d.* FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
        CASE WHEN i.[Id] IS NULL THEN NULL ELSE (SELECT i.* FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
        SYSUTCDATETIME()
    FROM inserted i
    FULL OUTER JOIN deleted d ON i.[Id] = d.[Id];
END";

        private const string TracksTrigger = @"
CREATE OR ALTER TRIGGER [TR_Tracks_Audit] ON [Tracks]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO [AuditTrails] ([UserId], [TableName], [Action], [PrimaryKey], [OldValues], [NewValues], [ChangedAtUtc])
    SELECT
        'sql:' + ORIGINAL_LOGIN(),
        'Tracks',
        CASE WHEN d.[Id] IS NULL THEN 0 WHEN i.[Id] IS NULL THEN 2 ELSE 1 END,
        CAST(COALESCE(i.[Id], d.[Id]) AS nvarchar(200)),
        CASE WHEN d.[Id] IS NULL THEN NULL ELSE (SELECT d.* FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
        CASE WHEN i.[Id] IS NULL THEN NULL ELSE (SELECT i.* FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
        SYSUTCDATETIME()
    FROM inserted i
    FULL OUTER JOIN deleted d ON i.[Id] = d.[Id];
END";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CarsTrigger);
            migrationBuilder.Sql(TracksTrigger);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_Cars_Audit]");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_Tracks_Audit]");
        }
    }
}
