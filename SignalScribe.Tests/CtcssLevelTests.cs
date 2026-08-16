using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// CTCSS is transmitted at roughly 15% of system deviation, which sounds negligible — but 750us
/// de-emphasis is flat below its 212 Hz corner and rolls voice off above it, so a sub-audible tone
/// arrives at the recording louder than the speech. There is no high-pass anywhere on the audio
/// path it reached the Opus clip and the transcriber intact, holding 55-76% of the recorded energy
/// — the "hum" on 146.925 and the tone on 147.180. This pins that it is now filtered out.
/// </summary>
public class CtcssLevelTests(ITestOutputHelper output)
{
    private const double ChannelRate = 25_000;

    [Theory]
    [InlineData(67.0, 0.02)]
    [InlineData(100.0, 0.02)]
    [InlineData(131.8, 0.02)]
    [InlineData(203.5, 0.06)]
    [InlineData(254.1, 0.12)]   // a quarter-octave under the corner: real radios leak this one too
    public void TheSquelchToneIsFilteredOutOfWhatWeRecord(double ctcssHz, double allowedExcess)
    {
        // Standard practice: CTCSS at ~15% of system deviation (750 Hz of 5 kHz), voice at ~3 kHz.
        const double VoiceDev = 3_000, CtcssDev = 750;

        var demod = new NbfmDemodulator(ChannelRate);
        var pcm = new float[16_000 * 3];
        var n = demod.Process(Fm(2.0, ctcssHz, VoiceDev, CtcssDev), pcm);

        var seg = pcm[(n / 4)..(n * 3 / 4)];
        var (below300, voiceBand) = SplitAt300Hz(seg);
        var ratio = below300 / Math.Max(1e-12, below300 + voiceBand);

        // Compare against the same audio with no tone on it. The floor is not zero — voice energy
        // near the boundary and the measuring filter's own transition band both land in it — so the
        // question is what the tone *adds*, not what is left.
        var control = new NbfmDemodulator(ChannelRate);
        var cpcm = new float[16_000 * 3];
        var cn = control.Process(Fm(2.0, ctcssHz, VoiceDev, 0), cpcm);
        var (cLow, cVoice) = SplitAt300Hz(cpcm[(cn / 4)..(cn * 3 / 4)]);
        var baseline = cLow / Math.Max(1e-12, cLow + cVoice);

        output.WriteLine(
            $"  CTCSS {ctcssHz,6:F1} Hz -> sub-300 Hz holds {ratio * 100,5:F1}% of the audio "
            + $"vs {baseline * 100,5:F1}% with no tone -> the tone adds {(ratio - baseline) * 100,5:F1}%");

        Assert.True(
            ratio - baseline < allowedExcess,
            $"a {ctcssHz} Hz tone should not survive the high-pass; it added {(ratio - baseline):P1}");


    }

    /// <summary>
    /// Split the audio at 300 Hz with a 4th-order Butterworth high-pass and compare energy either
    /// side. Filtering rather than sampling the spectrum: a Goertzel sweep on a fixed frequency grid
    /// silently misses any tone that falls between its steps, which is most of the CTCSS set.
    /// </summary>
    private static (double Below, double Above) SplitAt300Hz(float[] x)
    {
        var hp1 = new Hp(300, 16_000);
        var hp2 = new Hp(300, 16_000);
        double total = 0, above = 0;
        foreach (var v in x)
        {
            total += v * v;
            var h = hp2.Process(hp1.Process(v));
            above += h * h;
        }

        return (Math.Max(0, total - above), above);
    }

    private sealed class Hp
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        public Hp(double fc, double fs)
        {
            var w0 = 2 * Math.PI * fc / fs;
            var cos = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2 * (1 / Math.Sqrt(2)));
            var a0 = 1 + alpha;
            _b0 = (1 + cos) / 2 / a0;
            _b1 = -(1 + cos) / a0;
            _b2 = (1 + cos) / 2 / a0;
            _a1 = -2 * cos / a0;
            _a2 = (1 - alpha) / a0;
        }

        public float Process(float x)
        {
            var y = (_b0 * x) + (_b1 * _x1) + (_b2 * _x2) - (_a1 * _y1) - (_a2 * _y2);
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return (float)y;
        }
    }

    /// <summary>FM carrier modulated by speech-band audio plus a sub-audible CTCSS tone.</summary>
    private static float[] Fm(double seconds, double ctcssHz, double voiceDev, double ctcssDev)
    {
        var n = (int)(ChannelRate * seconds);
        var iq = new float[n * 2];
        double phase = 0;
        var rng = new Random(3);
        for (var i = 0; i < n; i++)
        {
            var t = i / ChannelRate;
            // Speech-ish: a couple of formants with syllabic envelope.
            var syll = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 4 * t);
            var voice = syll * (0.6 * Math.Sin(2 * Math.PI * 520 * t) + 0.4 * Math.Sin(2 * Math.PI * 1180 * t));
            var ctcss = Math.Sin(2 * Math.PI * ctcssHz * t);

            phase += 2 * Math.PI * (voiceDev * voice + ctcssDev * ctcss) / ChannelRate;
            iq[2 * i] = (float)(0.4 * Math.Cos(phase) + (rng.NextDouble() * 2 - 1) * 0.002);
            iq[2 * i + 1] = (float)(0.4 * Math.Sin(phase) + (rng.NextDouble() * 2 - 1) * 0.002);
        }

        return iq;
    }
}
