using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Symbol timing recovery. The probe that established digital framing is viable used *ideal* timing;
/// this is what replaces that assumption, so it has to hold up against the things a real transmitter
/// does — a clock that is not quite 4800 baud, an arbitrary starting phase, and payload with no
/// helpful structure in it.
/// </summary>
public class SymbolSynchronizerTests(ITestOutputHelper output)
{
    private const double Rate = 25_000;
    private const double Baud = 4_800;

    [Theory]
    [InlineData(0)]
    [InlineData(50)]        // a few tens of ppm is an ordinary crystal
    [InlineData(-50)]
    [InlineData(200)]
    [InlineData(-200)]
    public void RecoversEverySymbolThroughAClockThatIsNotQuiteRight(double ppm)
    {
        var truth = TwoLevel(2_000, seed: 5);
        var waveform = Render(truth, Baud * (1 + (ppm / 1e6)), phaseOffset: 0.0);

        var (recovered, _) = Recover(waveform);
        var errors = CompareAfterLock(truth, recovered, out var compared);

        output.WriteLine($"  {ppm,5:F0} ppm -> {errors}/{compared} symbol errors");
        Assert.Equal(0, errors);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]       // worst case: the loop starts sampling exactly between symbols
    [InlineData(0.75)]
    public void LocksFromAnyStartingPhase(double phaseOffset)
    {
        var truth = TwoLevel(2_000, seed: 6);
        var waveform = Render(truth, Baud, phaseOffset);

        var (recovered, _) = Recover(waveform);
        var errors = CompareAfterLock(truth, recovered, out var compared);

        output.WriteLine($"  phase {phaseOffset:F2} -> {errors}/{compared} symbol errors");
        Assert.Equal(0, errors);
    }

    [Fact]
    public void TracksFourLevelSymbolsAsWellAsTwo()
    {
        // The detector never looks at symbol values, so C4FM should cost it nothing — worth pinning,
        // because a timing loop that only worked on two levels would quietly halve the plan.
        var truth = FourLevel(2_000, seed: 7);
        var waveform = Render(truth, Baud, phaseOffset: 0.3);

        var (recovered, _) = Recover(waveform);
        var errors = CompareAfterLock(truth, recovered, out var compared, fourLevel: true);

        output.WriteLine($"  four-level -> {errors}/{compared} symbol errors");
        Assert.Equal(0, errors);
    }

    [Fact]
    public void ConvergesOnTheRealClockRate()
    {
        var truth = TwoLevel(4_000, seed: 8);
        var waveform = Render(truth, Baud * 1.0001, phaseOffset: 0.1);   // +100 ppm

        var (_, sync) = Recover(waveform);
        output.WriteLine($"  measured {sync.ClockErrorPpm:F0} ppm, {sync.SamplesPerSymbol:F4} samples/symbol");

        // Sign and rough size matter; the loop is not a frequency counter.
        Assert.InRange(sync.ClockErrorPpm, 20, 400);
    }

    [Fact]
    public void ProducesRoughlyOneSymbolPerSymbolPeriod()
    {
        var truth = TwoLevel(1_000, seed: 9);
        var waveform = Render(truth, Baud, phaseOffset: 0);

        // Count against the waveform actually rendered, not the symbol array: Render drops the last
        // few symbols because their pulse tails would run off the end.
        var expected = waveform.Length / (Rate / Baud);

        var (recovered, _) = Recover(waveform);

        // Priming the interpolation window costs the first sample or two; anything beyond that is a
        // clock running at the wrong rate, which would slide the framer off its sync forever.
        Assert.InRange(recovered.Count, expected - 3, expected + 3);
    }

    private static (List<double> Symbols, SymbolSynchronizer Sync) Recover(float[] waveform)
    {
        var sync = new SymbolSynchronizer(Rate, Baud);
        var symbols = new List<double>(waveform.Length);
        foreach (var sample in waveform)
        {
            if (sync.Feed(sample, out var symbol))
            {
                symbols.Add(symbol);
            }
        }

        return (symbols, sync);
    }

    /// <summary>
    /// Compares recovered symbols against what was sent, once the loop has settled and allowing the
    /// constant symbol offset that timing recovery alone cannot resolve — a real receiver settles
    /// that with a frame sync pattern, which is the framer's job rather than this one's.
    /// </summary>
    private static int CompareAfterLock(double[] truth, List<double> recovered, out int compared, bool fourLevel = false)
    {
        const int Settle = 60;
        var best = int.MaxValue;
        compared = 0;

        for (var shift = -3; shift <= 3; shift++)
        {
            int errors = 0, counted = 0;
            for (var k = Settle; k < truth.Length - Settle; k++)
            {
                var index = k + shift;
                if (index < 0 || index >= recovered.Count)
                {
                    continue;
                }

                if (Slice(recovered[index], fourLevel) != Slice(truth[k], fourLevel))
                {
                    errors++;
                }

                counted++;
            }

            if (counted > 0 && errors < best)
            {
                best = errors;
                compared = counted;
            }
        }

        return best;
    }

    /// <summary>Nearest nominal level. Two-level is a sign test; four-level needs the thirds.</summary>
    private static int Slice(double value, bool fourLevel)
    {
        if (!fourLevel)
        {
            return value >= 0 ? 1 : -1;
        }

        return value switch
        {
            >= 1_296 => 3,
            >= 0 => 1,
            >= -1_296 => -1,
            _ => -3,
        };
    }

    private static double[] TwoLevel(int count, int seed)
    {
        var rng = new Random(seed);
        var symbols = new double[count];
        for (var i = 0; i < count; i++)
        {
            symbols[i] = rng.Next(2) == 0 ? 1_200 : -1_200;
        }

        return symbols;
    }

    private static double[] FourLevel(int count, int seed)
    {
        var rng = new Random(seed);
        double[] levels = [1_944, 648, -648, -1_944];
        var symbols = new double[count];
        for (var i = 0; i < count; i++)
        {
            symbols[i] = levels[rng.Next(levels.Length)];
        }

        return symbols;
    }

    /// <summary>Raised-cosine shaping at a possibly-wrong baud, starting at an arbitrary phase.</summary>
    private static float[] Render(double[] symbols, double baud, double phaseOffset)
    {
        var samplesPerSymbol = Rate / baud;
        var n = (int)((symbols.Length - 8) * samplesPerSymbol);
        var waveform = new float[n];

        for (var i = 0; i < n; i++)
        {
            var centre = (i / samplesPerSymbol) + phaseOffset;
            var first = Math.Max(0, (int)Math.Floor(centre) - 4);
            var last = Math.Min(symbols.Length - 1, (int)Math.Ceiling(centre) + 4);

            double sum = 0;
            for (var k = first; k <= last; k++)
            {
                sum += symbols[k] * RaisedCosine(centre - k, 0.2);
            }

            waveform[i] = (float)sum;
        }

        return waveform;
    }

    private static double RaisedCosine(double u, double beta)
    {
        if (Math.Abs(u) < 1e-9)
        {
            return 1.0;
        }

        var scaled = 2 * beta * u;
        var denominator = 1 - (scaled * scaled);
        if (Math.Abs(denominator) < 1e-9)
        {
            return Math.PI / 4 * Sinc(1 / (2 * beta));
        }

        return Sinc(u) * Math.Cos(Math.PI * beta * u) / denominator;
    }

    private static double Sinc(double u) => Math.Abs(u) < 1e-9 ? 1.0 : Math.Sin(Math.PI * u) / (Math.PI * u);
}
