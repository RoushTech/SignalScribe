using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelConfigAndNetSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_Nets_NetId",
                table: "Sessions");

            migrationBuilder.RenameColumn(
                name: "RecurrenceJson",
                table: "Nets",
                newName: "StartTimeUtc");

            migrationBuilder.AddColumn<int>(
                name: "DayOfWeekUtc",
                table: "Nets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Nets",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationMinutes",
                table: "Nets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Nets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Callsign",
                table: "Channels",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CtcssToneHz",
                table: "Channels",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Channels",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_Nets_NetId",
                table: "Sessions",
                column: "NetId",
                principalTable: "Nets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_Nets_NetId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "DayOfWeekUtc",
                table: "Nets");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Nets");

            migrationBuilder.DropColumn(
                name: "DurationMinutes",
                table: "Nets");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Nets");

            migrationBuilder.DropColumn(
                name: "Callsign",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "CtcssToneHz",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Channels");

            migrationBuilder.RenameColumn(
                name: "StartTimeUtc",
                table: "Nets",
                newName: "RecurrenceJson");

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_Nets_NetId",
                table: "Sessions",
                column: "NetId",
                principalTable: "Nets",
                principalColumn: "Id");
        }
    }
}
