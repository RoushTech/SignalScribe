using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitorRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MonitorHighHz",
                table: "CaptureSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "MonitorLowHz",
                table: "CaptureSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.UpdateData(
                table: "CaptureSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "MonitorHighHz", "MonitorLowHz" },
                values: new object[] { 148000000L, 144000000L });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonitorHighHz",
                table: "CaptureSettings");

            migrationBuilder.DropColumn(
                name: "MonitorLowHz",
                table: "CaptureSettings");
        }
    }
}
