using SignalScribe.Capture.Dsp;
using SignalScribe.Enums;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// The mode classifier reads the discriminator's level structure to name a modulation. These drive it
/// with synthesized deviation waveforms at the rate the channelizer produces (25 kSPS), which is the
/// same level <see cref="SubaudibleDetectorTests"/> works at.
///
/// The negative cases matter as much as the positive ones: a classifier that always answers *something*
/// would let every kerchunk and burst of noise create a channel, which is precisely what the voice gate
/// exists to prevent.
/// </summary>
public class ModeClassifierTests(ITestOutputHelper output)
{
    private const double Rate = 25_000;

    [Theory]
    [InlineData(1_944, 648)]    // DMR
    [InlineData(1_800, 600)]    // P25 Phase 1, YSF and NXDN96 all share this plan
    public void SeesC4fmAsDigitalWithoutNamingWhichC4fm(double outer, double inner)
    {
        // Four levels is a real, useful finding. Which of the four-level modes it is, is not
        // recoverable from deviation: the plans sit 8% apart, and the channel filter alone moves the
        // measurement 4-6% (see DigitalModeDiag). Sync patterns settle it; the framers own that.
        var mode = Classify(C4fm(1.0, outer, inner, baud: 4_800, seed: 2));
        Assert.Equal(DetectedMode.DigitalUnknown, mode);
    }

    [Fact]
    public void FourHumpsThatAreNotAThreeToOnePlanAreNotCalledDigital()
    {
        // C4FM places its inner symbols at a third of the outer. Four peaks without that ratio are
        // some other structure, and calling them digital would earn a channel on nothing.
        var mode = Classify(C4fm(1.5, outer: 3_000, inner: 1_900, baud: 4_800, seed: 12));
        Assert.Equal(DetectedMode.AnalogFm, mode);
    }

    [Fact]
    public void RecognisesDStarFromItsTwoLevelShift()
    {
        var mode = Classify(Fsk(1.0, deviation: 1_200, baud: 4_800, seed: 3, rolloff: 0.5));
        Assert.Equal(DetectedMode.DStar, mode);
    }

    [Fact]
    public void RecognisesPocsagFromItsWideShift()
    {
        var mode = Classify(Fsk(1.0, deviation: 4_500, baud: 1_200, seed: 4, rolloff: 0.2));
        Assert.Equal(DetectedMode.Pocsag, mode);
    }

    [Fact]
    public void LeavesAfskUnnamedForTheDecoderToConfirm()
    {
        // A Bell 202 burst is a sine sweeping between two tones, so its deviation histogram has two
        // humps at the sine's extremes and reads as an anonymous two-level signal. That is the right
        // answer here: a CRC-valid AX.25 frame out of the soft-TNC identifies APRS definitively, and
        // no statistic should pre-empt it.
        var mode = Classify(Afsk(1.0, deviation: 3_000, seed: 5));
        Assert.Equal(DetectedMode.DigitalUnknown, mode);
    }

    [Fact]
    public void CallsSpeechAnalogRatherThanDigital()
    {
        var mode = Classify(Voice(2.0, deviation: 3_000));
        Assert.Equal(DetectedMode.AnalogFm, mode);
    }

    [Fact]
    public void DoesNotInventAModeFromNoise()
    {
        var rng = new Random(7);
        var noise = new float[(int)(Rate * 1.5)];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)(Gaussian(rng) * 2_000);
        }

        var mode = Classify(noise);
        Assert.False(mode.IsIdentified(), $"noise classified as {mode}");
    }

    [Fact]
    public void OffersNoVerdictOnAnUnmodulatedCarrier()
    {
        // A kerchunk is a flat discriminator. It must not read as a recognised mode, or the voice gate
        // would let it create a channel.
        var carrier = new float[(int)(Rate * 1.0)];
        var mode = Classify(carrier);
        Assert.False(mode.IsIdentified(), $"dead carrier classified as {mode}");
    }

    [Fact]
    public void WaitsForEnoughSignalBeforeCommitting()
    {
        // A tenth of a second of DMR is real DMR, but not yet enough histogram to be sure of.
        var mode = Classify(C4fm(0.1, outer: 1_944, inner: 648, baud: 4_800, seed: 8));
        Assert.Equal(DetectedMode.Unknown, mode);
    }

    [Fact]
    public void ASteadyToneIsNotADigitalVoiceMode()
    {
        // A sine's histogram peaks hard at its extremes, which is a two-level structure. It must not
        // be allowed to land on D-STAR or POCSAG just because the amplitude happens to suit.
        var tone = new float[(int)(Rate * 1.5)];
        for (var i = 0; i < tone.Length; i++)
        {
            tone[i] = (float)(3_000 * Math.Sin(2 * Math.PI * 1_200 * i / Rate));
        }

        var mode = Classify(tone);
        Assert.True(mode is DetectedMode.DigitalUnknown or DetectedMode.AnalogFm, $"got {mode}");
    }

    [Theory]
    [InlineData(-200)]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(500)]
    public void StillSeesC4fmThroughTransmitterDeviationError(double errorHz)
    {
        // Real radios are not on their nominal deviation, and the channel filter pulls the levels in
        // besides. None of that should cost us the finding that matters — that this is digital.
        var outer = 1_944 + errorHz;
        var mode = Classify(C4fm(1.0, outer, outer / 3, baud: 4_800, seed: 9));
        output.WriteLine($"  deviation error {errorHz,5:F0} Hz -> {mode}");
        Assert.Equal(DetectedMode.DigitalUnknown, mode);
    }

    [Theory]
    [InlineData(1_200)]     // D-STAR nominal
    [InlineData(1_100)]     // compressed by the channel filter and a low transmitter
    [InlineData(1_350)]
    public void DStarSurvivesTheCompressionTheChannelFilterApplies(double deviation)
    {
        // Two-level modes are named, unlike four-level ones, because D-STAR and POCSAG sit nearly
        // four times apart — far outside anything filtering or deviation error can do.
        Assert.Equal(DetectedMode.DStar, Classify(Fsk(1.0, deviation, baud: 4_800, seed: 13, rolloff: 0.5)));
    }

    private DetectedMode Classify(float[] deviation)
    {
        var c = new ModeClassifier(Rate);
        foreach (var s in deviation)
        {
            c.Feed(s);
        }

        var mode = c.Classify();
        output.WriteLine($"  {mode}: {c.LastScore}");
        return mode;
    }

    private static float[] C4fm(double seconds, double outer, double inner, double baud, int seed)
        => DigitalSignals.C4fm(Rate, seconds, outer, inner, baud, seed);

    private static float[] Fsk(double seconds, double deviation, double baud, int seed, double rolloff)
        => DigitalSignals.Fsk(Rate, seconds, deviation, baud, seed, rolloff);

    private static float[] Afsk(double seconds, double deviation, int seed)
        => DigitalSignals.Afsk(Rate, seconds, deviation, seed);

    /// <summary>Speech-like deviation: a few formants under a syllabic envelope, the shape the voice gate is tuned on.</summary>
    private static float[] Voice(double seconds, double deviation)
    {
        var n = (int)(Rate * seconds);
        var outBuf = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = i / Rate;
            var envelope = 0.5 + (0.5 * Math.Sin(2 * Math.PI * 4 * t));
            var s = (0.6 * Math.Sin(2 * Math.PI * 300 * t))
                  + (0.3 * Math.Sin(2 * Math.PI * 900 * t))
                  + (0.2 * Math.Sin(2 * Math.PI * 1_700 * t));
            outBuf[i] = (float)(deviation * envelope * s / 1.1);
        }

        return outBuf;
    }

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
