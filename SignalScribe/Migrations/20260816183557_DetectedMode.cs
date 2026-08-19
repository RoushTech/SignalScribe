using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class DetectedMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "Transmissions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "DiscardedClips",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Modulation",
                table: "Channels",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Transmissions");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "DiscardedClips");

            migrationBuilder.DropColumn(
                name: "Modulation",
                table: "Channels");
        }
    }
}
