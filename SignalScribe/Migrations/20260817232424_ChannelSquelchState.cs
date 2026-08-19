using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class ChannelSquelchState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdaptiveSquelch",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                // True, not the scaffolder's false: adaptive tracking is what every existing
                // channel has been doing, and defaulting to false would silently pin every floor
                // on this band the moment the migration ran.
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "DcsCode",
                table: "Channels",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdaptiveSquelch",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "DcsCode",
                table: "Channels");
        }
    }
}
