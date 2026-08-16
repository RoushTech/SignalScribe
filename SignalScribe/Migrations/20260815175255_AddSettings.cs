using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class AddSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaptureSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CenterFrequencyHz = table.Column<long>(type: "INTEGER", nullable: false),
                    SampleRateHz = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelSpacingHz = table.Column<int>(type: "INTEGER", nullable: false),
                    GainReductionDb = table.Column<int>(type: "INTEGER", nullable: false),
                    LnaState = table.Column<int>(type: "INTEGER", nullable: false),
                    AgcEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SquelchOpenDb = table.Column<double>(type: "REAL", nullable: false),
                    SquelchCloseDb = table.Column<double>(type: "REAL", nullable: false),
                    SquelchHangMs = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkerSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WhisperModel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TranscriptionPrompt = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SummaryModel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MaxJobsPerClaim = table.Column<int>(type: "INTEGER", nullable: false),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "CaptureSettings",
                columns: new[] { "Id", "AgcEnabled", "CenterFrequencyHz", "ChannelSpacingHz", "GainReductionDb", "LnaState", "SampleRateHz", "SquelchCloseDb", "SquelchHangMs", "SquelchOpenDb" },
                values: new object[] { 1, true, 146000000L, 12500, 40, 0, 6000000L, 5.0, 400, 8.0 });

            migrationBuilder.InsertData(
                table: "WorkerSettings",
                columns: new[] { "Id", "MaxJobsPerClaim", "Paused", "SummaryModel", "TranscriptionPrompt", "WhisperModel" },
                values: new object[] { 1, 4, false, "Qwen2.5-7B-Instruct-Q4_K_M.gguf", "Amateur radio net. QSL, QRZ, seventy-three, net control, check-in, kerchunk, repeater, simplex, CQ, destinated.", "ggml-small.en-q5_1.bin" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaptureSettings");

            migrationBuilder.DropTable(
                name: "WorkerSettings");
        }
    }
}
