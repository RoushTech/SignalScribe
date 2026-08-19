using SignalScribe.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Filling the Whisper window before spending a run on it. The saving is the whole point, so it is
/// measured here rather than asserted in the abstract.
/// </summary>
public class BatchGatherTests(ITestOutputHelper output)
{
    [Fact]
    public void AnEmptyWindowKeepsGathering()
    {
        Assert.True(BatchGather.ShouldKeepGathering(gatheredMs: 2_000, waitedMs: 0, budgetMs: 20_000));
    }

    [Fact]
    public void AFullWindowStopsEarly()
    {
        // Past the target the next span opens a second window anyway, so waiting shares nothing.
        Assert.False(BatchGather.ShouldKeepGathering(gatheredMs: 25_000, waitedMs: 1_000, budgetMs: 20_000));
    }

    [Fact]
    public void TheDeadlineBoundsTheWaitOnAQuietBand()
    {
        // One clip and nothing else coming: it must still run, or a lone over would sit forever.
        Assert.False(BatchGather.ShouldKeepGathering(gatheredMs: 1_500, waitedMs: 20_000, budgetMs: 20_000));
    }

    [Fact]
    public void ZeroBudgetDisablesGatheringEntirely()
    {
        Assert.False(BatchGather.ShouldKeepGathering(gatheredMs: 0, waitedMs: 0, budgetMs: 0));
    }

    /// <summary>
    /// The measured shape of the problem: worker logs showed runs carrying 1.1–7.6 s of audio into a
    /// 27 s window. Packed together those same clips share one run instead of taking five.
    /// </summary>
    [Fact]
    public void GatheringACoversationTurnsManyRunsIntoOne()
    {
        int[] overs = [7_600, 4_100, 3_800, 1_300, 1_100];

        var ungathered = overs.Sum(o => BatchGather.RunsFor([o]));
        var gathered = BatchGather.RunsFor(overs);

        output.WriteLine($"  {overs.Length} overs, {overs.Sum() / 1000.0:F1}s audio");
        output.WriteLine($"  one run each: {ungathered} runs (~{ungathered * 7}s wall)");
        output.WriteLine($"  gathered:     {gathered} run  (~{gathered * 7}s wall)");

        Assert.Equal(5, ungathered);
        Assert.Equal(1, gathered);
    }

    [Fact]
    public void ALongOverStillGetsItsOwnRun()
    {
        // Longer than a window: Whisper splits it internally, and it must not drag others with it.
        Assert.Equal(1, BatchGather.RunsFor([40_000]));
        Assert.Equal(2, BatchGather.RunsFor([40_000, 5_000]));
    }

    [Fact]
    public void GatheringNeverOverfillsAWindow()
    {
        // Twelve five-second overs cannot share one 27-second window; the packer must open more.
        var overs = Enumerable.Repeat(5_000, 12).ToList();
        var runs = BatchGather.RunsFor(overs);

        output.WriteLine($"  12 × 5s = 60s audio packed into {runs} runs");
        Assert.True(runs >= 3, $"{runs} runs cannot hold 60 s of audio in 27 s windows");
    }
}
