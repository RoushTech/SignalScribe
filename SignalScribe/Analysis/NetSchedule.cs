namespace SignalScribe.Analysis;

/// <summary>
/// Does a session start inside a net's declared window? Pure, so the window arithmetic can be
/// tested without standing up the sessionization service.
///
/// A net's schedule is a UTC time-of-day plus an optional UTC day of week. **No day means daily** —
/// traffic and emergency nets commonly run every evening, and a nullable day already carried the
/// right shape, so nothing about the schema had to change to say it.
/// </summary>
public static class NetSchedule
{
    /// <summary>Check-ins start trickling in before the announced time.</summary>
    public const int LeadInMinutes = 10;

    /// <summary>
    /// Nets run long; a late check-in still belongs to the net. Note the lead-in shifts the whole
    /// window earlier rather than only extending its front, so the allowance past the *announced*
    /// end is this minus <see cref="LeadInMinutes"/>.
    /// </summary>
    public const int RunOverMinutes = 20;

    /// <summary>Assumed length when the operator did not declare one.</summary>
    public const int DefaultDurationMinutes = 60;

    /// <param name="dayOfWeekUtc">The UTC day the window opens, or null for a net that runs every day.</param>
    public static bool Matches(DayOfWeek? dayOfWeekUtc, TimeOnly startTimeUtc, int? durationMinutes, DateTime sessionStartUtc) =>
        TryGetWindow(dayOfWeekUtc, startTimeUtc, durationMinutes, sessionStartUtc, out _, out _);

    /// <summary>
    /// The specific occurrence a moment falls in, not merely whether one does.
    ///
    /// The window's start is what identifies an occurrence, and that identity is what keeps a net
    /// from being shredded into fragments. A net is a conversation with long pauses — check-ins
    /// trickle in, net control waits, someone goes to look something up — and observed on air a
    /// single three-hour net produced nineteen separate sessions, gaps of up to fifteen minutes
    /// between overs against a ninety-second join rule. Nineteen sessions means nineteen summaries
    /// of two transmissions each rather than one summary of the net. Inside a declared window the
    /// window itself is the boundary, so the gap rule does not apply.
    /// </summary>
    public static bool TryGetWindow(
        DayOfWeek? dayOfWeekUtc,
        TimeOnly startTimeUtc,
        int? durationMinutes,
        DateTime atUtc,
        out DateTime windowStartUtc,
        out DateTime windowEndUtc)
    {
        windowStartUtc = default;
        windowEndUtc = default;
        var duration = TimeSpan.FromMinutes(durationMinutes ?? DefaultDurationMinutes);

        // Three candidate windows: the one opening on the session's own UTC date, the one that
        // opened the previous UTC day and is still running, and tomorrow's — whose lead-in can
        // reach back across midnight. Testing only the session's own date makes any window that
        // straddles midnight UTC unmatchable from the far side, and that is the common case rather
        // than an edge case: an evening net anywhere in the Americas lands within a couple of hours
        // of UTC midnight. Daily nets made it obvious, but weekly nets were always wrong there too.
        for (var offset = -1; offset <= 1; offset++)
        {
            var windowDate = atUtc.Date.AddDays(offset);

            // The declared day is the day the window *opens*, not the day the session starts — a
            // net announced for Monday that runs past midnight is still Monday's net.
            if (dayOfWeekUtc is { } day && windowDate.DayOfWeek != day)
            {
                continue;
            }

            var windowStart = windowDate + startTimeUtc.ToTimeSpan() - TimeSpan.FromMinutes(LeadInMinutes);
            var windowEnd = windowStart + duration + TimeSpan.FromMinutes(RunOverMinutes);
            if (atUtc >= windowStart && atUtc <= windowEnd)
            {
                windowStartUtc = windowStart;
                windowEndUtc = windowEnd;
                return true;
            }
        }

        return false;
    }
}
