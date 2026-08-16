using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class DefaultSampleRate64M : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "CaptureSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "SampleRateHz",
                value: 6400000L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "CaptureSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "SampleRateHz",
                value: 6000000L);
        }
    }
}
