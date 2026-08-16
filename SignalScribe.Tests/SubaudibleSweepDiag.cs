using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

public class SubaudibleSweepDiagnostics(ITestOutputHelper output)
{
    private const double Rate = 25_000;

    [Fact]
    public void WhereDoesCtcssDetectionFallOver()
    {
        output.WriteLine("  CTCSS: tone deviation share vs over length");
        foreach (var seconds in new[] { 0.8, 1.2, 2.0, 4.0 })
        {
            var row = $"    {seconds,4:F1}s: ";
            foreach (var toneAmp in new[] { 0.30, 0.20, 0.12, 0.08, 0.05 })
            {
                var d = new SubaudibleDetector(Rate);
                Feed(d, seconds, n => Voice(n) + ((float)toneAmp * MathF.Sin(2 * MathF.PI * 131.8f * n / (float)Rate)));
                row += $"{toneAmp:F2}={(d.Ctcss() is null ? "-" : "Y")}  ";
            }

            output.WriteLine(row);
        }

        output.WriteLine("  DCS: same, with bit errors added");
        foreach (var seconds in new[] { 0.8, 1.2, 2.0, 4.0 })
        {
            var row = $"    {seconds,4:F1}s: ";
            foreach (var errorRate in new[] { 0.0, 0.01, 0.03, 0.08 })
            {
                var word = DcsCodes.Encode(073);
                var rng = new Random(11);
                var d = new SubaudibleDetector(Rate);
                Feed(d, seconds, n =>
                {
                    var bit = (int)(n * 134.4 / Rate) % 23;
                    var level = ((word >> bit) & 1) == 1 ? 0.2f : -0.2f;
                    if (rng.NextDouble() < errorRate / 200.0)
                    {
                        level = -level;
                    }

                    return Voice(n) + level;
                });
                row += $"err{errorRate:F2}={(d.Dcs() == 073 ? "Y" : "-")}  ";
            }

            output.WriteLine(row);
        }
    }

    private static void Feed(SubaudibleDetector d, double seconds, Func<int, float> gen)
    {
        var n = (int)(Rate * seconds);
        for (var i = 0; i < n; i++)
        {
            d.Feed(gen(i));
        }
    }

    private static float Voice(int n)
    {
        var t = n / Rate;
        var syllable = 0.55 + (0.45 * Math.Sin(2 * Math.PI * 4 * t));
        return (float)(syllable * 0.4 * (Math.Sin(2 * Math.PI * 520 * t) + (0.6 * Math.Sin(2 * Math.PI * 1180 * t))));
    }
}
