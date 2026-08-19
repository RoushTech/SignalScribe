using SignalScribe.Analysis;
using Xunit;

namespace SignalScribe.Tests;

/// <summary>
/// Whether a session can absorb a transmission. The out-of-order case is the one that matters:
/// live traffic hides the bug because it always arrives in time order.
/// </summary>
public class SessionContinuityTests
{
    private static readonly TimeSpan JoinGap = TimeSpan.FromSeconds(90);

    private static readonly DateTime SessionStart = new(2026, 8, 19, 3, 21, 39, DateTimeKind.Utc);

    private static readonly DateTime SessionEnd = new(2026, 8, 19, 3, 49, 20, DateTimeKind.Utc);

    [Fact]
    public void ATransmissionInsideTheSessionIsAbsorbed()
    {
        Assert.True(SessionContinuity.CanAbsorb(SessionStart, SessionEnd, SessionStart.AddMinutes(5), JoinGap));
    }

    [Fact]
    public void ATransmissionJustAfterTheEndContinuesIt()
    {
        Assert.True(SessionContinuity.CanAbsorb(SessionStart, SessionEnd, SessionEnd.AddSeconds(60), JoinGap));
    }

    [Fact]
    public void ATransmissionPastTheJoinGapStartsANewSession()
    {
        Assert.False(SessionContinuity.CanAbsorb(SessionStart, SessionEnd, SessionEnd.AddSeconds(120), JoinGap));
    }

    /// <summary>
    /// The regression. Re-sessionizing hands back transmissions from hours earlier, and the gap to a
    /// later session is negative — which passes any bare "within the join gap" test. Measured, that
    /// put 980 transmissions from one afternoon into a single 28-minute session the following day.
    /// </summary>
    [Fact]
    public void ASessionCannotAbsorbATransmissionOlderThanItself()
    {
        var yesterday = SessionStart.AddDays(-1);

        Assert.False(SessionContinuity.CanAbsorb(SessionStart, SessionEnd, yesterday, JoinGap));
    }

    [Fact]
    public void ASessionCannotReachBackwardsEvenByASecond()
    {
        Assert.False(SessionContinuity.CanAbsorb(SessionStart, SessionEnd, SessionStart.AddSeconds(-1), JoinGap));
    }
}
