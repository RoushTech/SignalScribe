using System.Diagnostics;
using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

public class SubaudibleCostTests(ITestOutputHelper output)
{
    private const double Rate = 25_000;

    /// <summary>
    /// Whether tone detection can run on every transmission or only where a channel has none set
    /// comes down to what it costs. It taps the demodulator, so it runs once per *open gate*, not
    /// once per channel in the bank.
    /// </summary>
    [Fact]
    public void CostPerOpenGateIsANegligibleFractionOfRealtime()
    {
        const int Seconds = 60;
        var d = new SubaudibleDetector(Rate);
        var samples = new float[(int)Rate * Seconds];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 520 * i / Rate)
                               + 0.15 * Math.Sin(2 * Math.PI * 100 * i / Rate));
        }

        // Warm up the JIT before timing.
        for (var i = 0; i < 25_000; i++)
        {
            d.Feed(samples[i]);
        }

        var timed = new SubaudibleDetector(Rate);
        var sw = Stopwatch.StartNew();
        foreach (var s in samples)
        {
            timed.Feed(s);
        }

        _ = timed.Ctcss();
        _ = timed.Dcs();
        sw.Stop();

        var realtimeFraction = sw.Elapsed.TotalSeconds / Seconds;
        output.WriteLine(
            $"  {Seconds}s of audio in {sw.Elapsed.TotalMilliseconds:F0} ms "
            + $"= {realtimeFraction * 100:F3}% of one core per open gate "
            + $"({realtimeFraction * 32 * 100:F1}% with all {32} gates open)");

        Assert.True(realtimeFraction < 0.02, $"detector cost {realtimeFraction:P2} of realtime per gate");
    }
}
