using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
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
                    SquelchHangMs = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviationHz = table.Column<double>(type: "REAL", nullable: false),
                    MonitorLowHz = table.Column<long>(type: "INTEGER", nullable: false),
                    MonitorHighHz = table.Column<long>(type: "INTEGER", nullable: false),
                    DeviceSerial = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FrequencyHz = table.Column<long>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoDisabledReason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LastSpeechUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Callsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CtcssToneHz = table.Column<double>(type: "REAL", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    NoiseFloorDbfs = table.Column<double>(type: "REAL", nullable: true),
                    LearnedStateJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscardedClips",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FrequencyHz = table.Column<long>(type: "INTEGER", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AudioPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PeakDbfs = table.Column<double>(type: "REAL", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    VoicedMs = table.Column<int>(type: "INTEGER", nullable: false),
                    SpeechBandRatio = table.Column<double>(type: "REAL", nullable: false),
                    ModulationDepth = table.Column<double>(type: "REAL", nullable: false),
                    SyllableRateHz = table.Column<double>(type: "REAL", nullable: false),
                    SustainedTone = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscardedClips", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LeasedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Speakers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Callsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EmbeddingCentroid = table.Column<byte[]>(type: "BLOB", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FirstHeardUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastHeardUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Speakers", x => x.Id);
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
                    TranscriptionThreads = table.Column<int>(type: "INTEGER", nullable: false),
                    SummaryThreads = table.Column<int>(type: "INTEGER", nullable: false),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    DiscardRetentionHours = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Nets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    DayOfWeekUtc = table.Column<int>(type: "INTEGER", nullable: true),
                    StartTimeUtc = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Nets_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsNet = table.Column<bool>(type: "INTEGER", nullable: false),
                    NetId = table.Column<long>(type: "INTEGER", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryModel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Sessions_Nets_NetId",
                        column: x => x.NetId,
                        principalTable: "Nets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Transmissions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AudioPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PeakDbfs = table.Column<double>(type: "REAL", nullable: false),
                    MeanCarrierOffsetHz = table.Column<double>(type: "REAL", nullable: true),
                    IsDouble = table.Column<bool>(type: "INTEGER", nullable: false),
                    VoicedMs = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<long>(type: "INTEGER", nullable: true),
                    TranscribedByModel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transmissions_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Transmissions_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Markers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TransmissionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    OffsetMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Markers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Markers_Transmissions_TransmissionId",
                        column: x => x.TransmissionId,
                        principalTable: "Transmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Segments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TransmissionId = table.Column<long>(type: "INTEGER", nullable: false),
                    StartMs = table.Column<int>(type: "INTEGER", nullable: false),
                    EndMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Transcript = table.Column<string>(type: "TEXT", nullable: true),
                    TranscriptionModel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SpeakerEmbedding = table.Column<byte[]>(type: "BLOB", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Callsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SpeakerId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Segments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Segments_Speakers_SpeakerId",
                        column: x => x.SpeakerId,
                        principalTable: "Speakers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Segments_Transmissions_TransmissionId",
                        column: x => x.TransmissionId,
                        principalTable: "Transmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "CaptureSettings",
                columns: new[] { "Id", "AgcEnabled", "CenterFrequencyHz", "ChannelSpacingHz", "DeviationHz", "DeviceSerial", "GainReductionDb", "LnaState", "MonitorHighHz", "MonitorLowHz", "SampleRateHz", "SquelchCloseDb", "SquelchHangMs", "SquelchOpenDb" },
                values: new object[] { 1, true, 146000000L, 12500, 5000.0, null, 40, 0, 148000000L, 144000000L, 6400000L, 5.0, 400, 8.0 });

            migrationBuilder.InsertData(
                table: "WorkerSettings",
                columns: new[] { "Id", "DiscardRetentionHours", "MaxJobsPerClaim", "Paused", "SummaryModel", "SummaryThreads", "TranscriptionPrompt", "TranscriptionThreads", "WhisperModel" },
                values: new object[] { 1, 24, 4, false, "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf", 0, "Amateur radio net. QSL, QRZ, seventy-three, net control, check-in, kerchunk, repeater, simplex, CQ, destinated.", 0, "ggml-small.en-q5_1.bin" });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_FrequencyHz",
                table: "Channels",
                column: "FrequencyHz",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscardedClips_StartUtc",
                table: "DiscardedClips",
                column: "StartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status_Type",
                table: "Jobs",
                columns: new[] { "Status", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_Markers_TransmissionId_OffsetMs",
                table: "Markers",
                columns: new[] { "TransmissionId", "OffsetMs" });

            migrationBuilder.CreateIndex(
                name: "IX_Nets_ChannelId",
                table: "Nets",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Segments_Callsign",
                table: "Segments",
                column: "Callsign");

            migrationBuilder.CreateIndex(
                name: "IX_Segments_SpeakerId",
                table: "Segments",
                column: "SpeakerId");

            migrationBuilder.CreateIndex(
                name: "IX_Segments_TransmissionId",
                table: "Segments",
                column: "TransmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ChannelId_StartUtc",
                table: "Sessions",
                columns: new[] { "ChannelId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_NetId",
                table: "Sessions",
                column: "NetId");

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_Callsign",
                table: "Speakers",
                column: "Callsign");

            migrationBuilder.CreateIndex(
                name: "IX_Transmissions_ChannelId_StartUtc",
                table: "Transmissions",
                columns: new[] { "ChannelId", "StartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transmissions_SessionId",
                table: "Transmissions",
                column: "SessionId");

            // FTS5 full-text index over segment transcripts. Virtual tables can't be expressed in
            // the EF model, so this is the sanctioned raw-SQL escape hatch (CLAUDE.md): created
            // here, kept in sync by triggers, queried via the SegmentSearchHit keyless entity +
            // FromSql MATCH.
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE segment_fts USING fts5(
                    "Transcript",
                    content='Segments',
                    content_rowid='Id'
                );
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER segments_fts_insert AFTER INSERT ON "Segments" BEGIN
                    INSERT INTO segment_fts(rowid, "Transcript") VALUES (new."Id", new."Transcript");
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER segments_fts_delete AFTER DELETE ON "Segments" BEGIN
                    INSERT INTO segment_fts(segment_fts, rowid, "Transcript") VALUES ('delete', old."Id", old."Transcript");
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER segments_fts_update AFTER UPDATE OF "Transcript" ON "Segments" BEGIN
                    INSERT INTO segment_fts(segment_fts, rowid, "Transcript") VALUES ('delete', old."Id", old."Transcript");
                    INSERT INTO segment_fts(rowid, "Transcript") VALUES (new."Id", new."Transcript");
                END;
                """);
        }


        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS segments_fts_update;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS segments_fts_delete;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS segments_fts_insert;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS segment_fts;");

            migrationBuilder.DropTable(
                name: "CaptureSettings");

            migrationBuilder.DropTable(
                name: "DiscardedClips");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Markers");

            migrationBuilder.DropTable(
                name: "Segments");

            migrationBuilder.DropTable(
                name: "WorkerSettings");

            migrationBuilder.DropTable(
                name: "Speakers");

            migrationBuilder.DropTable(
                name: "Transmissions");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Nets");

            migrationBuilder.DropTable(
                name: "Channels");
        }
    }
}
