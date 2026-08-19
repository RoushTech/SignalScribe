using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class DigitalHeaderFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HeaderFieldsJson",
                table: "Segments",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeaderFieldsJson",
                table: "Segments");
        }
    }
}
