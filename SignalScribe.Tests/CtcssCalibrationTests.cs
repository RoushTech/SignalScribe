using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// The calibration behind the CTCSS thresholds, kept so they cannot be retuned blind. Records what
/// each class of input actually scores; the assertion is on the *separation*, not on any one value.
/// </summary>
public class CtcssCalibrationTests(ITestOutputHelper output)
{
    private const double Rate = 25_000;

    [Fact]
    public void RealTonesAndEverythingElseStaySeparated()
    {
        output.WriteLine($"  {"input",-28} {"winner",8} {"margin",8} {"peak",8} {"share",8}");
        Show("CTCSS 146.2 under voice", n => Voice(n) + Tone(n, 146.2, 0.20));
        Show("CTCSS 100.0 under voice", n => Voice(n) + Tone(n, 100.0, 0.20));
        Show("CTCSS 146.2, weak", n => Voice(n) + Tone(n, 146.2, 0.08));
        Show("120 Hz mains hum", n => Voice(n) + Tone(n, 120.0, 0.25));
        Show("60 Hz mains hum", n => Voice(n) + Tone(n, 60.0, 0.25));
        Show("voice only, no tone", Voice);
        Show("DCS 073 bitstream", n =>
        {
            var word = DcsCodes.Encode(073);
            var bit = (int)(n * 134.4 / Rate) % 23;
            return Voice(n) + (((word >> bit) & 1) == 1 ? 0.2f : -0.2f);
        });

        AssertSeparation();
    }

    private readonly List<(string Label, bool IsTone, double Margin, double Peak, double Share)> _scores = [];

    private void Show(string label, Func<int, float> gen)
    {
        var d = new SubaudibleDetector(Rate);
        var n = (int)(Rate * 2.5);
        for (var i = 0; i < n; i++)
        {
            d.Feed(gen(i));
        }

        var ctcss = d.Ctcss();
        var (tone, margin, peak, share) = d.LastScore;
        output.WriteLine(
            $"  {label,-28} {tone,8:F1} {margin,8:F2} {peak,8:F2} {share,8:F3}   "
            + $"-> ctcss={(ctcss?.ToString("F1") ?? "none"),6}  dcs={(d.Dcs()?.ToString("D3") ?? "none")}");

        _scores.Add((label, label.StartsWith("CTCSS", StringComparison.Ordinal), margin, peak, share));
    }

    private void AssertSeparation()
    {
        var tones = _scores.Where(s => s.IsTone).ToList();
        var others = _scores.Where(s => !s.IsTone).ToList();

        // Every real tone must outscore every non-tone on all three measures, with room to spare.
        Assert.True(tones.Min(t => t.Margin) > 10 * others.Max(o => o.Margin), "margin separation collapsed");
        Assert.True(tones.Min(t => t.Share) > 10 * others.Max(o => o.Share), "share separation collapsed");
        Assert.True(tones.Min(t => t.Peak) > others.Max(o => o.Peak), "peak separation collapsed");
    }

    private static float Tone(int n, double hz, double amp) => (float)(amp * Math.Sin(2 * Math.PI * hz * n / Rate));

    /// <summary>
    /// Speech as it reaches this detector. Two things matter and both are easy to get wrong: the
    /// pitch fundamental lives inside the CTCSS range and wanders, so it is the real thing a tone
    /// must be told apart from; and a transmitter pre-emphasises voice (+6 dB/octave) but injects
    /// CTCSS/DCS *after* that stage, so at the discriminator the voice's low end is far weaker than
    /// its raw level. Modelling voice without pre-emphasis makes the sub-audible band look ~20 dB
    /// dirtier than it is, and tuning against that fiction cripples the detector.
    /// </summary>
    private static float Voice(int n)
    {
        var t = n / Rate;
        var syllable = 0.55 + (0.45 * Math.Sin(2 * Math.PI * 4 * t));
        var pitch = 118 + (22 * Math.Sin(2 * Math.PI * 1.7 * t));

        // Pre-emphasis: 6 dB/octave above the 212 Hz corner, i.e. weight by f/212 where f > 212.
        double Pre(double hz) => Math.Max(hz / 212.0, 0.08);

        double v = 0.9 * Pre(pitch) * Math.Sin(2 * Math.PI * pitch * t);
        v += 0.5 * Pre(2 * pitch) * Math.Sin(2 * Math.PI * 2 * pitch * t);
        v += 0.6 * Pre(520) * Math.Sin(2 * Math.PI * 520 * t);
        v += 0.35 * Pre(1180) * Math.Sin(2 * Math.PI * 1180 * t);
        v += (0.25 * Rumble.NextDouble()) - 0.125;
        return (float)(syllable * 0.12 * v);
    }

    private static readonly Random Rumble = new(19);
}
