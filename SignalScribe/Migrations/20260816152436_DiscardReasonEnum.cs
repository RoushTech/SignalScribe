using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalScribe.Migrations
{
    /// <inheritdoc />
    public partial class DiscardReasonEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows carry a free-text reason. Every measurement the classifier uses is
            // already stored per row, so re-derive the enum here in the same order of confidence
            // rather than dropping the operator's discard history on the floor.
            migrationBuilder.Sql("""
                UPDATE "DiscardedClips" SET "Reason" = CASE
                    WHEN "Reason" LIKE '%overload%'      THEN 2
                    WHEN "Reason" LIKE '%ms of signal%'  THEN 1
                    WHEN "SustainedTone" = 1             THEN 3
                    WHEN "SpeechBandRatio" < 0.3         THEN 4
                    WHEN "SyllableRateHz" > 12           THEN 5
                    WHEN "ModulationDepth" <= 0.30       THEN 7
                    WHEN "VoicedMs" < 800                THEN 6
                    ELSE 0 END;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "Reason",
                table: "DiscardedClips",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Reason",
                table: "DiscardedClips",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.Sql("""
                UPDATE "DiscardedClips" SET "Reason" = CASE "Reason"
                    WHEN '1' THEN 'only a moment of signal'
                    WHEN '2' THEN 'front-end overload'
                    WHEN '3' THEN 'steady tone'
                    WHEN '4' THEN 'outside the speech band'
                    WHEN '5' THEN 'too fast for speech'
                    WHEN '6' THEN 'not enough voice'
                    WHEN '7' THEN 'no syllable rhythm'
                    ELSE 'unknown' END;
                """);
        }
    }
}
