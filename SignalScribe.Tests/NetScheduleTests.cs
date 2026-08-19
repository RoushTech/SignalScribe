using SignalScribe.Analysis;
using Xunit;

namespace SignalScribe.Tests;

public class NetScheduleTests
{
    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    // 2026-08-17 is a Monday; 2026-08-18 a Tuesday.
    [Fact]
    public void WeeklyNetMatchesOnlyItsOwnDay()
    {
        var start = new TimeOnly(1, 0);
        Assert.True(NetSchedule.Matches(DayOfWeek.Monday, start, 60, Utc(2026, 8, 17, 1, 5)));
        Assert.False(NetSchedule.Matches(DayOfWeek.Monday, start, 60, Utc(2026, 8, 18, 1, 5)));
    }

    [Fact]
    public void DailyNetMatchesEveryDayAtItsTime()
    {
        var start = new TimeOnly(1, 0);
        foreach (var day in Enumerable.Range(17, 7))
        {
            Assert.True(NetSchedule.Matches(null, start, 60, Utc(2026, 8, day, 1, 5)));
        }

        // Still a window, not a free pass: the wrong time of day is not this net.
        Assert.False(NetSchedule.Matches(null, start, 60, Utc(2026, 8, 17, 14, 0)));
    }

    [Fact]
    public void LeadInAndRunOverBoundAreTheWindow()
    {
        // The lead-in shifts the whole window earlier, so a 60-minute net declared for 01:00 is
        // open 00:50 .. 02:10 — 10 minutes of it before the announced start, 10 minutes after the
        // announced end. Pinned here because it is easy to misread the run-over as 20 minutes past
        // 02:00.
        var start = new TimeOnly(1, 0);
        Assert.False(NetSchedule.Matches(null, start, 60, Utc(2026, 8, 17, 0, 49)));
        Assert.True(NetSchedule.Matches(null, start, 60, Utc(2026, 8, 17, 0, 50)));
        Assert.True(NetSchedule.Matches(null, start, 60, Utc(2026, 8, 17, 2, 10)));
        Assert.False(NetSchedule.Matches(null, start, 60, Utc(2026, 8, 17, 2, 11)));
    }

    /// <summary>
    /// An evening net in the Americas lands within a couple of hours of UTC midnight, so its window
    /// routinely straddles the date boundary. Checking only the session's own UTC date made the far
    /// side of that boundary unmatchable — the reason daily nets are what exposed this.
    /// </summary>
    [Fact]
    public void WindowStraddlingMidnightMatchesOnBothSides()
    {
        var start = new TimeOnly(23, 30);         // 19:30 US Eastern in summer

        // Before midnight, on the day the window opens.
        Assert.True(NetSchedule.Matches(null, start, 60, Utc(2026, 8, 17, 23, 35)));

        // After midnight, still inside the same 60-minute net.
        Assert.True(NetSchedule.Matches(null, start, 60, Utc(2026, 8, 18, 0, 15)));

        // And the lead-in reaches back across midnight for a net that starts just after it.
        Assert.True(NetSchedule.Matches(null, new TimeOnly(0, 5), 60, Utc(2026, 8, 17, 23, 56)));
    }

    /// <summary>
    /// The declared day is the day the window *opens*. A Monday-night net running past midnight is
    /// still Monday's net, and must not be claimed by an identical Tuesday one.
    /// </summary>
    [Fact]
    public void WeeklyNetOwnsItsRunOverPastMidnight()
    {
        var start = new TimeOnly(23, 30);
        var afterMidnight = Utc(2026, 8, 18, 0, 15); // Tuesday by the clock, Monday's net by the window

        Assert.True(NetSchedule.Matches(DayOfWeek.Monday, start, 60, afterMidnight));
        Assert.False(NetSchedule.Matches(DayOfWeek.Tuesday, start, 60, afterMidnight));
    }

    [Fact]
    public void MissingDurationFallsBackToTheDefault()
    {
        var start = new TimeOnly(1, 0);
        Assert.True(NetSchedule.Matches(null, start, null, Utc(2026, 8, 17, 2, 10)));
        Assert.False(NetSchedule.Matches(null, start, null, Utc(2026, 8, 17, 2, 11)));
    }
}
