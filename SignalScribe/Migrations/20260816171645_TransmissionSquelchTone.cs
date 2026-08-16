using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class TransmissionSquelchTone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CtcssHz",
                table: "Transmissions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DcsCode",
                table: "Transmissions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CtcssHz",
                table: "Transmissions");

            migrationBuilder.DropColumn(
                name: "DcsCode",
                table: "Transmissions");
        }
    }
}
