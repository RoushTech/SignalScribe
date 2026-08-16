using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Data;
using SignalScribe.Data.Models;
using Xunit;

namespace SignalScribe.Tests;

/// <summary>
/// DB-level tests run against in-memory SQLite with real migrations applied — never the EF
/// in-memory provider (CLAUDE.md), so SQL translation and the FTS5 virtual table are exercised.
/// </summary>
public sealed class DatabaseTests : IDisposable
{
    private readonly SqliteConnection _connection;

    private readonly SignalScribeContext _db;

    public DatabaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<SignalScribeContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new SignalScribeContext(options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void MigrationsCreateFtsTable()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'segment_fts'";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public async Task TranscriptIsSearchableThroughFts()
    {
        var channel = new Channel { FrequencyHz = 146_520_000, Label = "146.520 simplex" };
        var transmission = new Transmission
        {
            Channel = channel,
            StartUtc = DateTime.UtcNow,
            AudioPath = "clips/test.ogg",
        };
        _db.Segments.Add(new Segment
        {
            Transmission = transmission,
            StartMs = 0,
            EndMs = 4000,
            Transcript = "this is KD9ABC monitoring for the hamfest announcement",
            TranscriptionModel = "test/fixture",
        });
        await _db.SaveChangesAsync();

        var hits = await _db.SegmentSearchHits
            .FromSql($"""
                SELECT s."Id" AS "SegmentId",
                       snippet(segment_fts, 0, '<b>', '</b>', '…', 12) AS "Snippet",
                       bm25(segment_fts) AS "Rank"
                FROM segment_fts
                JOIN "Segments" s ON s."Id" = segment_fts.rowid
                WHERE segment_fts MATCH {"hamfest"}
                """)
            .ToListAsync();

        var hit = Assert.Single(hits);
        Assert.Contains("hamfest", hit.Snippet);
    }

    [Fact]
    public async Task FtsIndexFollowsUpdatesAndDeletes()
    {
        var segment = new Segment
        {
            Transmission = new Transmission
            {
                Channel = new Channel { FrequencyHz = 147_000_000, Label = "test" },
                StartUtc = DateTime.UtcNow,
                AudioPath = "clips/x.ogg",
            },
            StartMs = 0,
            EndMs = 1000,
            Transcript = "antenna party on saturday",
        };
        _db.Segments.Add(segment);
        await _db.SaveChangesAsync();

        segment.Transcript = "tower climb rescheduled";
        await _db.SaveChangesAsync();

        Assert.Empty(await SearchAsync("antenna"));
        Assert.Single(await SearchAsync("tower"));

        _db.Segments.Remove(segment);
        await _db.SaveChangesAsync();

        Assert.Empty(await SearchAsync("tower"));
    }

    [Fact]
    public async Task SettingsRowsAreSeededByMigration()
    {
        var capture = await _db.CaptureSettings.FindAsync(1);
        Assert.NotNull(capture);
        Assert.Equal(146_000_000, capture.CenterFrequencyHz);

        var workers = await _db.WorkerSettings.FindAsync(1);
        Assert.NotNull(workers);
        Assert.False(workers.Paused);
        Assert.Contains("net control", workers.TranscriptionPrompt);
    }

    [Fact]
    public async Task DuplicateIngestIsRejectedByUniqueIndex()
    {
        var start = DateTime.UtcNow;
        var channel = new Channel { FrequencyHz = 146_940_000, Label = "repeater" };
        _db.Transmissions.Add(new Transmission { Channel = channel, StartUtc = start, AudioPath = "a.ogg" });
        await _db.SaveChangesAsync();

        _db.Transmissions.Add(new Transmission { ChannelId = channel.Id, StartUtc = start, AudioPath = "b.ogg" });
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    private Task<List<SegmentSearchHit>> SearchAsync(string term) =>
        _db.SegmentSearchHits
            .FromSql($"""
                SELECT s."Id" AS "SegmentId", '' AS "Snippet", bm25(segment_fts) AS "Rank"
                FROM segment_fts
                JOIN "Segments" s ON s."Id" = segment_fts.rowid
                WHERE segment_fts MATCH {term}
                """)
            .ToListAsync();
}
