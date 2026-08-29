using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameTrackerBlazorServerApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogueSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "ServerVersionSequence");

            migrationBuilder.CreateTable(
                name: "AuditTrails",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TableName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    PrimaryKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OldValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditTrails", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ShortName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ServerVersion = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TelemetryRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GameId = table.Column<int>(type: "int", nullable: false),
                    CarExternalId = table.Column<int>(type: "int", nullable: false),
                    TrackExternalId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LapNumber = table.Column<int>(type: "int", nullable: false),
                    LapTime = table.Column<double>(type: "float", nullable: true),
                    IsValid = table.Column<bool>(type: "bit", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Cars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GameId = table.Column<int>(type: "int", nullable: false),
                    ExternalId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Manufacturer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Class = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Year = table.Column<int>(type: "int", nullable: true),
                    ServerVersion = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cars_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GameId = table.Column<int>(type: "int", nullable: false),
                    ExternalId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LayoutName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LengthMetres = table.Column<double>(type: "float", nullable: true),
                    ServerVersion = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tracks_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditTrails_ChangedAtUtc",
                table: "AuditTrails",
                column: "ChangedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditTrails_TableName_PrimaryKey",
                table: "AuditTrails",
                columns: new[] { "TableName", "PrimaryKey" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditTrails_UserId",
                table: "AuditTrails",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Cars_GameId_ExternalId",
                table: "Cars",
                columns: new[] { "GameId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cars_ServerVersion",
                table: "Cars",
                column: "ServerVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Games_ServerVersion",
                table: "Games",
                column: "ServerVersion");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryRecords_GameId_CarExternalId",
                table: "TelemetryRecords",
                columns: new[] { "GameId", "CarExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryRecords_GameId_TrackExternalId",
                table: "TelemetryRecords",
                columns: new[] { "GameId", "TrackExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryRecords_SessionId",
                table: "TelemetryRecords",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryRecords_UserId",
                table: "TelemetryRecords",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_GameId_ExternalId",
                table: "Tracks",
                columns: new[] { "GameId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_ServerVersion",
                table: "Tracks",
                column: "ServerVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditTrails");

            migrationBuilder.DropTable(
                name: "Cars");

            migrationBuilder.DropTable(
                name: "TelemetryRecords");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropTable(
                name: "Games");

            migrationBuilder.DropSequence(
                name: "ServerVersionSequence");
        }
    }
}
