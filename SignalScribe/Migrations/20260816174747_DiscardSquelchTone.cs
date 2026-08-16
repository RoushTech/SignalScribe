using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class DiscardSquelchTone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CtcssHz",
                table: "DiscardedClips",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DcsCode",
                table: "DiscardedClips",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CtcssHz",
                table: "DiscardedClips");

            migrationBuilder.DropColumn(
                name: "DcsCode",
                table: "DiscardedClips");
        }
    }
}
