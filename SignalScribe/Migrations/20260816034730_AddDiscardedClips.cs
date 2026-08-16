using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscardedClips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DiscardRetentionHours",
                table: "WorkerSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DiscardedClips",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FrequencyHz = table.Column<long>(type: "INTEGER", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AudioPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PeakDbfs = table.Column<double>(type: "REAL", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    VoicedMs = table.Column<int>(type: "INTEGER", nullable: false),
                    SpeechBandRatio = table.Column<double>(type: "REAL", nullable: false),
                    ModulationDepth = table.Column<double>(type: "REAL", nullable: false),
                    SyllableRateHz = table.Column<double>(type: "REAL", nullable: false),
                    SustainedTone = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscardedClips", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "WorkerSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "DiscardRetentionHours",
                value: 24);

            migrationBuilder.CreateIndex(
                name: "IX_DiscardedClips_StartUtc",
                table: "DiscardedClips",
                column: "StartUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscardedClips");

            migrationBuilder.DropColumn(
                name: "DiscardRetentionHours",
                table: "WorkerSettings");
        }
    }
}
