using System.Diagnostics;
using SignalScribe.Capture.Digital.Ysf;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// What the Fusion framer costs, because searching 192 decoder variants is only defensible if
/// capture can still keep up with the sample stream.
///
/// The shape of the cost matters more than the total: the variant search runs only when a 40-bit
/// frame sync has matched, which on anything that is not Fusion essentially never happens. So an
/// idle gate pays for a shift register and nothing else, and only a real Fusion carrier pays for
/// the search — ten times a second, which is where the budget has to hold.
/// </summary>
public class YsfCostTests(ITestOutputHelper output)
{
    [Fact]
    public void AnIdleGateCostsAlmostNothing()
    {
        // 30 seconds of analog-looking noise through the framer: no sync, so no Viterbi ever runs.
        const int Symbols = 4_800 * 30;
        var rng = new Random(3);
        var noise = new double[Symbols];
        for (var i = 0; i < Symbols; i++)
        {
            noise[i] = (rng.NextDouble() * 3) - 1.5;
        }

        var warm = new YsfFramer();
        foreach (var s in noise.AsSpan(0, 10_000).ToArray())
        {
            warm.Feed(s);
        }

        var best = double.MaxValue;
        for (var pass = 0; pass < 3; pass++)
        {
            var framer = new YsfFramer();
            var sw = Stopwatch.StartNew();
            foreach (var s in noise)
            {
                framer.Feed(s);
            }

            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalSeconds);
        }

        var fraction = best / 30.0;
        output.WriteLine($"  {best * 1000:F1} ms for 30 s — {fraction * 100:F4}% of one core per idle gate");
        Assert.True(fraction < 0.01, $"idle gate cost {fraction:P3} of one core");
    }

    [Fact]
    public void AFusionCarrierStaysInsideTheBudget()
    {
        // A real Fusion carrier: a frame every 100 ms, each triggering the full variant search.
        var frames = 300; // 30 seconds
        var symbols = Transmission(frames);

        var best = double.MaxValue;
        for (var pass = 0; pass < 3; pass++)
        {
            var framer = new YsfFramer();
            var sw = Stopwatch.StartNew();
            foreach (var s in symbols)
            {
                framer.Feed(s);
            }

            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalSeconds);
        }

        var fraction = best / 30.0;
        output.WriteLine($"  {YsfFichDecoder.Variants.Length} variants, {frames} frames");
        output.WriteLine($"  {best * 1000:F0} ms for 30 s — {fraction * 100:F2}% of one core per Fusion gate");

        // This carrier settles on a variant within three frames and then narrows to it, so what is
        // measured here is the steady state — 0.2% of a core, against 10% while still searching.
        // The bound is set well below that searching figure on purpose: if narrowing ever breaks,
        // the cost jumps back by fifty times and this test is what says so.
        Assert.True(fraction < 0.02, $"Fusion gate cost {fraction:P2} of one core — has variant narrowing regressed?");
    }

    private static double[] Transmission(int frames)
    {
        var variant = YsfFichDecoder.Variants[0];
        var rng = new Random(5);
        var symbols = new List<double>(frames * 480);
        for (var frame = 0; frame < frames; frame++)
        {
            for (var i = 19; i >= 0; i--)
            {
                symbols.Add(Level((int)((YsfFramer.FrameSync >> (i * 2)) & 3)));
            }

            var soft = new double[YsfFichDecoder.Dibits * 2];
            YsfFichDecoder.Encode([0x20, 0x01, 0x08, 0x40], variant, soft);
            for (var i = 0; i < YsfFichDecoder.Dibits; i++)
            {
                symbols.Add(Level((int)((soft[2 * i] > 0.5 ? 2 : 0) + (soft[(2 * i) + 1] > 0.5 ? 1 : 0))));
            }

            for (var i = 0; i < 360; i++)
            {
                symbols.Add(Level(rng.Next(4)));
            }
        }

        return [.. symbols];
    }

    private static double Level(int dibit) => dibit switch
    {
        0b01 => 1.5,
        0b00 => 0.5,
        0b10 => -0.5,
        _ => -1.5,
    };
}
