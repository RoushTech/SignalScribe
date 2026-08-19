using SignalScribe.Capture.Dsp;
using Xunit;

namespace SignalScribe.Tests;

/// <summary>
/// Bin-or-measurement to known-channel resolution. The failure this guards: an off-grid known
/// channel (147.180 on a 147.175 bin) losing its record-everything trust to an exact-match lookup.
/// </summary>
public class KnownFrequencyResolverTests
{
    private const long HalfBin = 6_250;

    [Fact]
    public void AnExactMatchResolves()
    {
        Assert.Equal(147_180_000, KnownFrequencyResolver.Nearest([147_180_000], 147_180_000, HalfBin));
    }

    [Fact]
    public void AnOffGridChannelResolvesFromItsBinCentre()
    {
        // 147.180 seen from the 147.175 bin — the on-air case that was being discarded.
        Assert.Equal(147_180_000, KnownFrequencyResolver.Nearest([147_180_000], 147_175_000, HalfBin));
    }

    [Fact]
    public void BeyondHalfABinDoesNotResolve()
    {
        // The next bin over must not claim the channel: its energy is not in that bin.
        Assert.Null(KnownFrequencyResolver.Nearest([147_180_000], 147_187_500, HalfBin));
    }

    [Fact]
    public void TheNearerOfTwoCoBinChannelsWins()
    {
        Assert.Equal(147_175_000, KnownFrequencyResolver.Nearest([147_180_000, 147_175_000], 147_176_000, HalfBin));
    }

    [Fact]
    public void NoKnownChannelsResolvesToNothing()
    {
        Assert.Null(KnownFrequencyResolver.Nearest([], 147_175_000, HalfBin));
    }
}
