using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <summary>
    /// FTS5 full-text index over segment transcripts. Virtual tables can't be expressed in the EF
    /// model, so this is the sanctioned raw-SQL escape hatch (CLAUDE.md): created here, kept in
    /// sync by triggers, queried via the SegmentSearchHit keyless entity + FromSql MATCH.
    /// </summary>
    public partial class AddTranscriptFts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
            migrationBuilder.Sql("DROP TRIGGER segments_fts_update;");
            migrationBuilder.Sql("DROP TRIGGER segments_fts_delete;");
            migrationBuilder.Sql("DROP TRIGGER segments_fts_insert;");
            migrationBuilder.Sql("DROP TABLE segment_fts;");
        }
    }
}
