using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class WorkerThreadSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SummaryThreads",
                table: "WorkerSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TranscriptionThreads",
                table: "WorkerSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "WorkerSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "SummaryThreads", "TranscriptionThreads" },
                values: new object[] { 0, 0 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SummaryThreads",
                table: "WorkerSettings");

            migrationBuilder.DropColumn(
                name: "TranscriptionThreads",
                table: "WorkerSettings");
        }
    }
}
