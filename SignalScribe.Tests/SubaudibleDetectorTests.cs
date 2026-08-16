using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

public class SubaudibleDetectorTests(ITestOutputHelper output)
{
    private const double Rate = 25_000;

    [Theory]
    [InlineData(67.0)]
    [InlineData(100.0)]
    [InlineData(131.8)]
    [InlineData(162.2)]   // one of the 1.5 Hz-spaced cluster: 159.8 / 162.2 / 165.5
    [InlineData(146.2)]   // 146.640 on air: reported as no tone, and as a phantom DCS code
    [InlineData(254.1)]
    public void ReadsTheCtcssToneOutFromUnderTheVoice(double toneHz)
    {
        var d = new SubaudibleDetector(Rate);
        Feed(d, 2.0, n => Voice(n) + (0.15f * MathF.Sin(2 * MathF.PI * (float)toneHz * n / (float)Rate)));

        Assert.Equal(toneHz, d.Ctcss());
    }

    [Fact]
    public void TellsApartToneslessThanTwoHertzApart()
    {
        foreach (var hz in new[] { 159.8, 162.2, 165.5 })
        {
            var d = new SubaudibleDetector(Rate);
            Feed(d, 2.0, n => Voice(n) + (0.15f * MathF.Sin(2 * MathF.PI * (float)hz * n / (float)Rate)));
            Assert.Equal(hz, d.Ctcss());
        }
    }

    [Fact]
    public void ReportsNothingWhenThereIsNoTone()
    {
        var plain = new SubaudibleDetector(Rate);
        Feed(plain, 2.0, Voice);
        Assert.Null(plain.Ctcss());

        // A hum that is not a standard tone must not be rounded to whichever one it sits nearest.
        var hum = new SubaudibleDetector(Rate);
        Feed(hum, 2.0, n => Voice(n) + (0.2f * MathF.Sin(2 * MathF.PI * 120f * n / (float)Rate)));
        var reported = hum.Ctcss();
        output.WriteLine($"  120 Hz mains hum reported as: {(reported?.ToString() ?? "nothing")}");
        Assert.Null(reported);
    }

    [Fact]
    public void WaitsForEnoughAudioBeforeCommitting()
    {
        var d = new SubaudibleDetector(Rate);
        Feed(d, 0.3, n => Voice(n) + (0.15f * MathF.Sin(2 * MathF.PI * 100f * n / (float)Rate)));
        Assert.Null(d.Ctcss());   // 300 ms cannot resolve 1.5 Hz spacing, so it says nothing
    }

    /// <summary>
    /// Every standard code through the whole waveform path. Two different repeaters on air both
    /// decoded as 073, which is either a coincidence or a decoder that leans on one answer — this
    /// is what tells them apart.
    /// </summary>
    [Fact]
    public void DecodesEveryStandardCodeAndNotJustAFavourite()
    {
        var wrong = new List<string>();
        foreach (var code in DcsCodes.Standard)
        {
            var word = DcsCodes.Encode(code);
            var d = new SubaudibleDetector(Rate);
            Feed(d, 2.5, n =>
            {
                var bit = (int)(n * 134.4 / Rate) % 23;
                return Voice(n) + (((word >> bit) & 1) == 1 ? 0.2f : -0.2f);
            });

            var got = d.Dcs();
            if (got != code)
            {
                wrong.Add($"{code:D3}->{(got?.ToString("D3") ?? "null")}");
            }
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count}/{DcsCodes.Standard.Length} wrong: {string.Join(" ", wrong.Take(12))}");
    }

    [Theory]
    [InlineData(023)]
    [InlineData(114)]
    [InlineData(754)]
    public void DecodesADcsCode(int code)
    {
        var word = DcsCodes.Encode(code);
        var d = new SubaudibleDetector(Rate);

        // DCS is the 23-bit word repeating forever at 134.4 bps, as a sub-audible square wave.
        Feed(d, 2.5, n =>
        {
            var bit = (int)(n * 134.4 / Rate) % 23;
            var level = ((word >> bit) & 1) == 1 ? 0.2f : -0.2f;
            return Voice(n) + level;
        });

        Assert.Equal(code, d.Dcs());
    }

    [Fact]
    public void DoesNotInventADcsCodeFromNoise()
    {
        var rng = new Random(5);
        var d = new SubaudibleDetector(Rate);
        Feed(d, 2.5, n => Voice(n) + (float)(rng.NextDouble() * 0.3 - 0.15));
        Assert.Null(d.Dcs());
    }

    /// <summary>
    /// 146.640 carries CTCSS 146.2 and was decoding as DCS 073. A tone beats against the 134.4 bps
    /// bit clock into a pattern that repeats and can satisfy the Golay check, so a channel with a
    /// tone must never also report a code — and DCS repeaters are rare enough that a phantom one is
    /// worse than none.
    /// </summary>
    [Theory]
    [InlineData(146.2)]
    [InlineData(131.8)]
    [InlineData(100.0)]
    public void ACtcssToneNeverDecodesAsAPhantomDcsCode(double toneHz)
    {
        var d = new SubaudibleDetector(Rate);
        Feed(d, 3.0, n => Voice(n) + (0.2f * MathF.Sin(2 * MathF.PI * (float)toneHz * n / (float)Rate)));

        Assert.Equal(toneHz, d.Ctcss());
        Assert.Null(d.Dcs());
    }

    [Fact]
    public void MainsHumIsNotRoundedToTheNearestStandardTone()
    {
        // 120 Hz sits 1.2 Hz off 118.8. Nearest-bin logic always yields *some* tone; the guard bins
        // either side are what let the answer be "none".
        //
        // 180 Hz is deliberately absent: the third harmonic of 60 Hz mains and CTCSS 179.9 are
        // 0.1 Hz apart, which no amount of thresholding separates inside one over — you would need
        // minutes of integration. Real radios cannot tell them apart either.
        foreach (var humHz in new[] { 120.0, 60.0 })
        {
            var d = new SubaudibleDetector(Rate);
            Feed(d, 2.0, n => Voice(n) + (0.25f * MathF.Sin(2 * MathF.PI * (float)humHz * n / (float)Rate)));
            Assert.Null(d.Ctcss());
            Assert.Null(d.Dcs());
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
