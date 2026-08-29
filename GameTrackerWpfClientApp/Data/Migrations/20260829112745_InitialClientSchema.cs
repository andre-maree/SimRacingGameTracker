using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameTrackerWpfClientApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialClientSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    GameId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Manufacturer = table.Column<string>(type: "TEXT", nullable: true),
                    Class = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    ServerVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ShortName = table.Column<string>(type: "TEXT", nullable: false),
                    ServerVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalTelemetry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameId = table.Column<int>(type: "INTEGER", nullable: false),
                    CarExternalId = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackExternalId = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LapNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    LapTime = table.Column<double>(type: "REAL", nullable: true),
                    IsValid = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    UploadedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalTelemetry", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameId = table.Column<int>(type: "INTEGER", nullable: false),
                    CarExternalId = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackExternalId = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionType = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndReason = table.Column<int>(type: "INTEGER", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncMetadata",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EntityName = table.Column<string>(type: "TEXT", nullable: false),
                    LastSyncedVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMetadata", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    GameId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    LayoutName = table.Column<string>(type: "TEXT", nullable: true),
                    Country = table.Column<string>(type: "TEXT", nullable: true),
                    LengthMetres = table.Column<double>(type: "REAL", nullable: true),
                    ServerVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StintNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OutLap = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Stints_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Laps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StintId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LapNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    LapTime = table.Column<double>(type: "REAL", nullable: true),
                    Sector1 = table.Column<double>(type: "REAL", nullable: true),
                    Sector2 = table.Column<double>(type: "REAL", nullable: true),
                    Sector3 = table.Column<double>(type: "REAL", nullable: true),
                    IsValid = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPitLap = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Laps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Laps_Stints_StintId",
                        column: x => x.StintId,
                        principalTable: "Stints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LapInputTelemetry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LapId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SampleRateHz = table.Column<int>(type: "INTEGER", nullable: false),
                    CompressedChannels = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Preview = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PreviewSampleCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LapInputTelemetry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LapInputTelemetry_Laps_LapId",
                        column: x => x.LapId,
                        principalTable: "Laps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cars_GameId_ExternalId",
                table: "Cars",
                columns: new[] { "GameId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LapInputTelemetry_LapId",
                table: "LapInputTelemetry",
                column: "LapId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Laps_StintId_LapNumber",
                table: "Laps",
                columns: new[] { "StintId", "LapNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_LocalTelemetry_SessionId",
                table: "LocalTelemetry",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalTelemetry_UploadedAtUtc_RecordedAtUtc",
                table: "LocalTelemetry",
                columns: new[] { "UploadedAtUtc", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_StartedAtUtc",
                table: "Sessions",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Stints_SessionId",
                table: "Stints",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncMetadata_EntityName",
                table: "SyncMetadata",
                column: "EntityName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_GameId_ExternalId",
                table: "Tracks",
                columns: new[] { "GameId", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Cars");

            migrationBuilder.DropTable(
                name: "Games");

            migrationBuilder.DropTable(
                name: "LapInputTelemetry");

            migrationBuilder.DropTable(
                name: "LocalTelemetry");

            migrationBuilder.DropTable(
                name: "SyncMetadata");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropTable(
                name: "Laps");

            migrationBuilder.DropTable(
                name: "Stints");

            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
