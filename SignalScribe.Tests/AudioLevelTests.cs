using SignalScribe.Capture.Dsp;
using Xunit;

namespace SignalScribe.Tests;

/// <summary>
/// Audio-path level discipline. Regression cover for "loud stations sound overdriven": the old
/// ±3 kHz full-scale reference clipped anything running standard ±5 kHz deviation.
/// </summary>
public class AudioLevelTests
{
    private const double ChannelRate = 25_000;

    [Theory]
    [InlineData(5_000)]   // standard amateur NBFM peak deviation
    [InlineData(7_500)]   // over-deviating station
    [InlineData(12_000)]  // badly over-deviating / noise burst
    public void AudioNeverExceedsFullScale(double deviationHz)
    {
        var demod = new NbfmDemodulator(ChannelRate);
        var pcm = Demodulate(demod, ToneDeviation(deviationHz, audioHz: 1_000, seconds: 0.5));

        // Everything must survive conversion at the pipeline's scale factor without clamping.
        foreach (var sample in pcm)
        {
            var scaled = sample * 30_000;
            Assert.InRange(scaled, short.MinValue, short.MaxValue);
        }
    }

    [Fact]
    public void StandardDeviationReachesHealthyLevelWithoutLimiting()
    {
        var demod = new NbfmDemodulator(ChannelRate);
        var pcm = Demodulate(demod, ToneDeviation(5_000, audioHz: 1_000, seconds: 0.5));

        var peak = 0f;
        foreach (var s in pcm[(pcm.Length / 4)..])
        {
            peak = MathF.Max(peak, MathF.Abs(s));
        }

        // De-emphasis attenuates a 1 kHz tone, so expect a solid but sub-unity peak.
        Assert.InRange(peak, 0.15f, 1.0f);
    }

    [Fact]
    public void LimiterIsTransparentBelowKneeAndCompressesAbove()
    {
        var quiet = new NbfmDemodulator(ChannelRate);
        var loud = new NbfmDemodulator(ChannelRate);
        var quietPeak = Peak(Demodulate(quiet, ToneDeviation(2_000, 1_000, 0.4)));
        var loudPeak = Peak(Demodulate(loud, ToneDeviation(20_000, 1_000, 0.4)));

        // 10× the deviation must not produce 10× the amplitude — and must stay bounded.
        Assert.True(loudPeak > quietPeak, "louder deviation should still be louder");
        Assert.True(loudPeak <= 1.0f, $"limiter must bound output (got {loudPeak})");
        Assert.True(loudPeak < quietPeak * 10, "limiter must compress above the knee");
    }

    [Fact]
    public void NarrowbandReferenceRaisesLevel()
    {
        var standard = Peak(Demodulate(new NbfmDemodulator(ChannelRate, 5_000), ToneDeviation(2_500, 1_000, 0.4)));
        var narrow = Peak(Demodulate(new NbfmDemodulator(ChannelRate, 2_500), ToneDeviation(2_500, 1_000, 0.4)));
        Assert.True(narrow > standard * 1.5, "narrowband reference should make the same deviation louder");
    }

    private static float[] ToneDeviation(double deviationHz, double audioHz, double seconds)
    {
        var n = (int)(ChannelRate * seconds);
        var iq = new float[n * 2];
        double phase = 0;
        for (var i = 0; i < n; i++)
        {
            var audio = Math.Sin(2 * Math.PI * audioHz * i / ChannelRate);
            phase += 2 * Math.PI * (deviationHz * audio) / ChannelRate;
            iq[2 * i] = (float)Math.Cos(phase);
            iq[2 * i + 1] = (float)Math.Sin(phase);
        }

        return iq;
    }

    private static float[] Demodulate(NbfmDemodulator demod, float[] iq)
    {
        var pcm = new float[iq.Length];
        var written = demod.Process(iq, pcm);
        return pcm[..written];
    }

    private static float Peak(float[] pcm)
    {
        var peak = 0f;
        foreach (var s in pcm[(pcm.Length / 4)..])
        {
            peak = MathF.Max(peak, MathF.Abs(s));
        }

        return peak;
    }
}

/// <summary>Bin-centre vs real channel grid: the filterbank's 12.5 kHz grid does not align with the 5 kHz channel plan.</summary>
public class ChannelGridTests
{
    [Theory]
    [InlineData(146_787_500, 2_500, 146_790_000)]    // 146.790 repeater on the 146.7875 bin
    [InlineData(146_787_500, 2_300, 146_790_000)]    // + a few ppm of receiver error
    [InlineData(146_787_500, 2_700, 146_790_000)]
    [InlineData(147_125_000, 60, 147_125_000)]       // already on grid — unchanged
    [InlineData(146_800_000, -120, 146_800_000)]
    [InlineData(145_000_000, -5_000, 144_995_000)]   // negative offset
    public void SnapsToRealChannel(long binHz, double offsetHz, long expected) =>
        Assert.Equal(expected, ChannelGrid.Snap(binHz, offsetHz));

    [Fact]
    public void IgnoresImplausibleOffsets()
    {
        // Beyond half a bin the offset measurement is not trustworthy — keep the bin frequency.
        Assert.Equal(146_787_500, ChannelGrid.Snap(146_787_500, 9_000));
        Assert.Equal(146_787_500, ChannelGrid.Snap(146_787_500, double.NaN));
    }
}

public class HallucinationFilterTests
{
    private const string Prompt = "Amateur radio net. QSL, QRZ, seventy-three, net control, check-in, kerchunk, repeater, simplex, CQ, destinated.";

    [Theory]
    [InlineData("QSL, three, net control, check-in, kerchu, and, the other one. QSL, three, net control")]
    [InlineData("net control check-in repeater simplex")]
    [InlineData("")]
    public void RejectsPromptEcho(string text) =>
        Assert.True(SignalScribe.Analysis.HallucinationFilter.IsPromptEcho(text, Prompt));

    [Theory]
    [InlineData("Good evening this is KD9ABC checking in from the north side with no traffic")]
    [InlineData("The hamfest moved to the fairgrounds next month, back to net control")]
    [InlineData("That's scary because my mom had that, she was going in about twice a week")]
    public void KeepsRealSpeech(string text) =>
        Assert.False(SignalScribe.Analysis.HallucinationFilter.IsPromptEcho(text, Prompt));

    [Theory]
    [InlineData("[beeping]")]        // courtesy tone, as seen on transmission 8
    [InlineData("[BLANK_AUDIO]")]    // squelch tail
    [InlineData("(static)")]
    [InlineData("*wind noise*")]
    public void RejectsNonSpeechAnnotation(string text) =>
        Assert.True(SignalScribe.Analysis.HallucinationFilter.IsNonSpeechAnnotation(text));

    [Theory]
    [InlineData("Yeah, I hear ya. We took a motorcycle ride yesterday.")]
    [InlineData("KD9ABC monitoring [that's my portable] on the machine")]
    public void KeepsSpeechContainingBrackets(string text) =>
        Assert.False(SignalScribe.Analysis.HallucinationFilter.IsNonSpeechAnnotation(text));
}

/// <summary>
/// Carrier-offset estimation drives the reported channel frequency. Regression cover for markers
/// showing bin centre instead of the real channel (e.g. 144.390 APRS reported as 144.3875).
/// </summary>
public class CarrierOffsetTests
{
    private const double ChannelRate = 25_000;

    [Theory]
    [InlineData(2_500)]    // 144.390 APRS on the 144.3875 bin
    [InlineData(-5_000)]   // 144.920 on the 144.925 bin — worst-case grid misalignment
    [InlineData(0)]
    public void ConvergesWithinOneShortBurst(double offsetHz)
    {
        var demod = new NbfmDemodulator(ChannelRate);
        // 300 ms — shorter than an APRS packet, far shorter than a voice over.
        demod.Process(Carrier(offsetHz, 0.3), new float[8_000]);

        Assert.True(demod.OffsetSettled, "estimate should be usable well inside a short burst");
        Assert.InRange(demod.AverageOffsetHz, offsetHz - 250, offsetHz + 250);
    }

    /// <summary>
    /// Observed on air: APRS on 144.390 kept being reported on the 144.3875 bin. The offset was
    /// averaged over the whole clip, and a short packet inside a clip padded by squelch hang is
    /// mostly tail — noise, which the discriminator reports as ~0 Hz. That dragged a true +2500 Hz
    /// down to a few hundred, far too little to snap onto the right channel.
    /// </summary>
    [Fact]
    public void SquelchTailDoesNotDragTheOffsetTowardTheBinCentre()
    {
        var demod = new NbfmDemodulator(ChannelRate);
        var pcm = new float[64_000];

        demod.Process(Carrier(2_500, 0.35), pcm);        // the packet
        demod.SignalPresent = false;
        demod.Process(Noise(1.05), pcm);                 // squelch tail: three times as long

        Assert.True(demod.OffsetSettled);
        Assert.InRange(demod.AverageOffsetHz, 2_250, 2_750);
    }

    private static float[] Noise(double seconds)
    {
        var rng = new Random(31);
        var n = (int)(ChannelRate * seconds);
        var iq = new float[n * 2];
        for (var i = 0; i < iq.Length; i++)
        {
            iq[i] = (float)((rng.NextDouble() * 2 - 1) * 0.01);
        }

        return iq;
    }

    [Fact]
    public void SettlesOnlyAfterEnoughSamples()
    {
        var demod = new NbfmDemodulator(ChannelRate);
        demod.Process(Carrier(2_500, 0.01), new float[400]); // 10 ms
        Assert.False(demod.OffsetSettled, "10 ms is not enough to trust the estimate");
    }

    /// <summary>Modulated carrier: modulation averages out, leaving the offset.</summary>
    private static float[] Carrier(double offsetHz, double seconds)
    {
        var n = (int)(ChannelRate * seconds);
        var iq = new float[n * 2];
        double phase = 0;
        for (var i = 0; i < n; i++)
        {
            var modulation = 2_000 * Math.Sin(2 * Math.PI * 1_200 * i / ChannelRate);
            phase += 2 * Math.PI * (offsetHz + modulation) / ChannelRate;
            iq[2 * i] = (float)Math.Cos(phase);
            iq[2 * i + 1] = (float)Math.Sin(phase);
        }

        return iq;
    }
}
