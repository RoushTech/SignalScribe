using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class NoSpeechRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NoSpeechRetentionHours",
                table: "WorkerSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "WorkerSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "NoSpeechRetentionHours",
                value: 72);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NoSpeechRetentionHours",
                table: "WorkerSettings");
        }
    }
}
