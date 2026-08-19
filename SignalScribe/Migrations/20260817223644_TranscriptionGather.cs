using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class TranscriptionGather : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TranscriptionGatherSeconds",
                table: "WorkerSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "WorkerSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "TranscriptionGatherSeconds",
                value: 20);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TranscriptionGatherSeconds",
                table: "WorkerSettings");
        }
    }
}
