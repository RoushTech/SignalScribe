using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Data;
using SignalScribe.Data.Models;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Picking sessions to summarise. Run against real SQLite rather than the in-memory provider,
/// because the whole point is that the eligibility rule survives translation into SQL — the bug
/// this covers was the rule being applied *after* the query instead of inside it.
/// </summary>
public sealed class SummaryCandidatesTests : IDisposable
{
    private readonly ITestOutputHelper output;

    private readonly SqliteConnection _connection;

    private readonly SignalScribeContext _db;

    private readonly Channel _channel = new() { FrequencyHz = 144_920_000, Label = "144.920" };

    public SummaryCandidatesTests(ITestOutputHelper testOutput)
    {
        output = testOutput;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new SignalScribeContext(new DbContextOptionsBuilder<SignalScribeContext>()
            .UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _db.Channels.Add(_channel);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void TheRuleItself()
    {
        Assert.True(SummaryCandidates.ShouldSummarize(isNet: true, TimeSpan.FromSeconds(30)));
        Assert.True(SummaryCandidates.ShouldSummarize(isNet: false, TimeSpan.FromMinutes(30)));
        Assert.False(SummaryCandidates.ShouldSummarize(isNet: false, TimeSpan.FromMinutes(2)));
    }

    /// <summary>
    /// The starvation this exists to prevent. Twenty-five short ragchews sit in front of the work
    /// that matters; with the cap applied before the eligibility rule, every pass fetched those and
    /// discarded them all, so nothing was ever summarised and nothing ever would be.
    /// </summary>
    [Fact]
    public async Task ShortRagchewsDoNotStarveTheQueue()
    {
        var start = new DateTime(2026, 8, 17, 1, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 25; i++)
        {
            AddSession(start.AddMinutes(i * 5), TimeSpan.FromMinutes(1), isNet: false, withTranscript: true);
        }

        var net = AddSession(start.AddHours(15), TimeSpan.FromMinutes(2), isNet: true, withTranscript: true);
        var longRagchew = AddSession(start.AddHours(16), TimeSpan.FromMinutes(25), isNet: false, withTranscript: true);
        await _db.SaveChangesAsync();

        var candidates = await SummaryCandidates.FetchAsync(_db, DateTime.UtcNow, take: 20, CancellationToken.None);

        output.WriteLine($"  {candidates.Count} candidates from 27 sessions (25 of them short ragchews)");
        Assert.Contains(candidates, s => s.Id == net.Id);
        Assert.Contains(candidates, s => s.Id == longRagchew.Id);
        Assert.All(candidates, s => Assert.True(s.IsNet || s.EndUtc - s.StartUtc >= SummaryCandidates.MinRagchew));
    }

    [Fact]
    public async Task ASessionWithNoTranscriptIsNotACandidate()
    {
        AddSession(DateTime.UtcNow.AddHours(-2), TimeSpan.FromMinutes(30), isNet: true, withTranscript: false);
        await _db.SaveChangesAsync();

        Assert.Empty(await SummaryCandidates.FetchAsync(_db, DateTime.UtcNow, 20, CancellationToken.None));
    }

    [Fact]
    public async Task AnAlreadySummarisedSessionIsNotACandidate()
    {
        var session = AddSession(DateTime.UtcNow.AddHours(-2), TimeSpan.FromMinutes(30), isNet: true, withTranscript: true);
        session.Summary = "already written";
        await _db.SaveChangesAsync();

        Assert.Empty(await SummaryCandidates.FetchAsync(_db, DateTime.UtcNow, 20, CancellationToken.None));
    }

    [Fact]
    public async Task ASessionStillInProgressIsNotACandidate()
    {
        // Ends after the quiet cutoff: someone may still be talking.
        AddSession(DateTime.UtcNow.AddMinutes(-1), TimeSpan.FromSeconds(30), isNet: true, withTranscript: true);
        await _db.SaveChangesAsync();

        var cutoff = DateTime.UtcNow.AddMinutes(-3);
        Assert.Empty(await SummaryCandidates.FetchAsync(_db, cutoff, 20, CancellationToken.None));
    }

    [Fact]
    public async Task TheNewestWorkIsServedFirst()
    {
        var old = AddSession(new DateTime(2026, 8, 17, 1, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(30), true, true);
        var recent = AddSession(new DateTime(2026, 8, 18, 1, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(30), true, true);
        await _db.SaveChangesAsync();

        var candidates = await SummaryCandidates.FetchAsync(_db, DateTime.UtcNow, take: 1, CancellationToken.None);

        Assert.Equal(recent.Id, Assert.Single(candidates).Id);
        Assert.NotEqual(old.Id, candidates[0].Id);
    }

    private Session AddSession(DateTime startUtc, TimeSpan duration, bool isNet, bool withTranscript)
    {
        var session = new Session
        {
            Channel = _channel,
            StartUtc = startUtc,
            EndUtc = startUtc + duration,
            IsNet = isNet,
        };

        var transmission = new Transmission
        {
            Channel = _channel,
            Session = session,
            StartUtc = startUtc,
            EndUtc = startUtc + duration,
            AudioPath = $"clips/{startUtc.Ticks}.ogg",
        };

        if (withTranscript)
        {
            transmission.Segments = [new Segment
            {
                StartMs = 0,
                EndMs = 1000,
                Transcript = "this is KD9ABC checking in",
                TranscriptionModel = "test/fixture",
            }];
        }

        _db.Sessions.Add(session);
        _db.Transmissions.Add(transmission);
        return session;
    }
}
