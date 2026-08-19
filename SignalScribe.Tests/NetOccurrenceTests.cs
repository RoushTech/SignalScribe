using SignalScribe.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Identifying which occurrence of a net a moment belongs to — the thing that keeps one net meeting
/// from becoming dozens of sessions.
/// </summary>
public class NetOccurrenceTests(ITestOutputHelper output)
{
    private static readonly TimeOnly Noon = new(16, 0);

    /// <summary>
    /// The observed failure, as arithmetic: a three-hour net on 144.920 produced nineteen sessions
    /// in one afternoon because overs were up to fifteen minutes apart and the join rule is ninety
    /// seconds. Every one of those instants has to resolve to the *same* window.
    /// </summary>
    [Fact]
    public void EveryOverOfOneNetResolvesToOneOccurrence()
    {
        DateTime[] overs =
        [
            new(2026, 8, 18, 16, 5, 0, DateTimeKind.Utc),
            new(2026, 8, 18, 16, 47, 0, DateTimeKind.Utc),
            new(2026, 8, 18, 17, 30, 0, DateTimeKind.Utc),
            new(2026, 8, 18, 18, 29, 0, DateTimeKind.Utc),
            new(2026, 8, 18, 18, 42, 50, DateTimeKind.Utc),
            new(2026, 8, 18, 18, 58, 18, DateTimeKind.Utc),
            new(2026, 8, 18, 19, 8, 20, DateTimeKind.Utc),
        ];

        var starts = new HashSet<DateTime>();
        foreach (var over in overs)
        {
            Assert.True(NetSchedule.TryGetWindow(null, Noon, 180, over, out var start, out _), $"{over:HH:mm} fell outside the net");
            starts.Add(start);
        }

        output.WriteLine($"  {overs.Length} overs spanning {(overs[^1] - overs[0]).TotalMinutes:F0} min → {starts.Count} occurrence(s)");
        Assert.Single(starts);
    }

    [Fact]
    public void ConsecutiveDaysAreDifferentOccurrences()
    {
        NetSchedule.TryGetWindow(null, Noon, 180, new DateTime(2026, 8, 18, 17, 0, 0, DateTimeKind.Utc), out var monday, out _);
        NetSchedule.TryGetWindow(null, Noon, 180, new DateTime(2026, 8, 19, 17, 0, 0, DateTimeKind.Utc), out var tuesday, out _);

        Assert.NotEqual(monday, tuesday);
        Assert.Equal(TimeSpan.FromDays(1), tuesday - monday);
    }

    [Fact]
    public void TrafficOutsideTheWindowBelongsToNoOccurrence()
    {
        // Well after the run-over allowance: this is someone chatting, not the net.
        Assert.False(NetSchedule.TryGetWindow(
            null, Noon, 180, new DateTime(2026, 8, 18, 21, 0, 0, DateTimeKind.Utc), out _, out _));
    }

    [Fact]
    public void TheWindowCoversTheLeadInAndTheRunOver()
    {
        // Check-ins start before the announced time and stragglers arrive after the announced end.
        Assert.True(NetSchedule.TryGetWindow(
            null, Noon, 180, new DateTime(2026, 8, 18, 15, 52, 0, DateTimeKind.Utc), out var start, out var end));

        Assert.Equal(new DateTime(2026, 8, 18, 15, 50, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 8, 18, 19, 10, 0, DateTimeKind.Utc), end);
    }

    [Fact]
    public void MatchesStillAgreesWithTheWindow()
    {
        // Matches is now a thin wrapper; the two must not drift apart.
        var inside = new DateTime(2026, 8, 18, 17, 0, 0, DateTimeKind.Utc);
        var outside = new DateTime(2026, 8, 18, 23, 0, 0, DateTimeKind.Utc);

        Assert.Equal(NetSchedule.Matches(null, Noon, 180, inside), NetSchedule.TryGetWindow(null, Noon, 180, inside, out _, out _));
        Assert.Equal(NetSchedule.Matches(null, Noon, 180, outside), NetSchedule.TryGetWindow(null, Noon, 180, outside, out _, out _));
    }

    [Fact]
    public void AWeeklyNetRunningPastMidnightKeepsOneOccurrence()
    {
        // A Sunday-evening net in the Americas opens Monday UTC and runs past midnight; both sides
        // must resolve to the window that *opened*, or it splits at midnight.
        var before = new DateTime(2026, 8, 17, 0, 30, 0, DateTimeKind.Utc);
        var after = new DateTime(2026, 8, 17, 1, 30, 0, DateTimeKind.Utc);

        Assert.True(NetSchedule.TryGetWindow(DayOfWeek.Monday, new TimeOnly(0, 0), 120, before, out var a, out _));
        Assert.True(NetSchedule.TryGetWindow(DayOfWeek.Monday, new TimeOnly(0, 0), 120, after, out var b, out _));
        Assert.Equal(a, b);
    }
}
