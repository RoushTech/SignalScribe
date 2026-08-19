using System.Diagnostics;
using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

public class ModeClassifierCostTests(ITestOutputHelper output)
{
    private const double Rate = 25_000;

    /// <summary>
    /// Whether mode classification can run on every transmission, or only where a channel has no mode
    /// recorded, comes down to what it costs. The interesting cases are precisely the mismatches — a
    /// DMR burst on a channel that is normally analog is a different system sharing the frequency — so
    /// it has to be unconditional, and that is only defensible if it is nearly free.
    ///
    /// It taps the demodulator, so it runs once per *open gate*, not once per channel in the bank.
    /// </summary>
    [Fact]
    public void CostPerOpenGateIsANegligibleFractionOfRealtime()
    {
        const int Seconds = 60;
        var samples = new float[(int)Rate * Seconds];
        var rng = new Random(11);
        for (var i = 0; i < samples.Length; i++)
        {
            // Four-level traffic: the branch-heaviest case for the histogram.
            samples[i] = (float)(((rng.Next(4) * 1_296) - 1_944) + (rng.NextDouble() * 100));
        }

        var warm = new ModeClassifier(Rate);
        for (var i = 0; i < 25_000; i++)
        {
            warm.Feed(samples[i]);
        }

        _ = warm.Classify();

        var timed = new ModeClassifier(Rate);
        var sw = Stopwatch.StartNew();
        foreach (var s in samples)
        {
            timed.Feed(s);
        }

        _ = timed.Classify();
        sw.Stop();

        var fractionOfOneCore = sw.Elapsed.TotalSeconds / Seconds;
        output.WriteLine($"  {sw.Elapsed.TotalMilliseconds:F1} ms for {Seconds}s of signal");
        output.WriteLine($"  {fractionOfOneCore * 100:F4}% of one core per open gate");
        output.WriteLine($"  {fractionOfOneCore * 100 * 32:F3}% with all 32 gates open");

        // Generous ceiling — the point is to catch a change that makes this a per-sample expense
        // worth reconsidering, not to pin the current number.
        Assert.True(fractionOfOneCore < 0.02, $"cost {fractionOfOneCore:P2} of one core per gate");
    }
}
