using SignalScribe.Analysis;
using SignalScribe.Enums;
using Xunit;

namespace SignalScribe.Tests;

public class DiscardClassifierTests
{
    /// <summary>A clip that would pass every test — the baseline the cases below each break one of.</summary>
    private static DiscardReason Classify(
        bool overload = false,
        double presentMs = 3_000,
        bool sustainedTone = false,
        double speechBandRatio = 0.7,
        double syllableRateHz = 4,
        int voicedMs = 2_000,
        double modulationDepth = 0.6,
        DetectedMode mode = DetectedMode.Unknown) =>
        DiscardClassifier.Classify(overload, presentMs, sustainedTone, speechBandRatio, syllableRateHz, voicedMs, modulationDepth, mode);

    [Fact]
    public void OverloadOutranksEverythingElse()
    {
        // Nothing measured during clipping is real, so no other test's verdict means anything.
        Assert.Equal(DiscardReason.FrontEndOverload, Classify(overload: true, sustainedTone: true, speechBandRatio: 0.01));
    }

    [Fact]
    public void AClickIsTooShortBeforeItIsAnythingElse()
    {
        Assert.Equal(DiscardReason.TooShort, Classify(presentMs: 150, speechBandRatio: 0.01));
        Assert.NotEqual(DiscardReason.TooShort, Classify(presentMs: DiscardClassifier.MinPresentMs));
    }

    [Fact]
    public void CarrierAndHeterodyneReadAsASteadyTone()
    {
        Assert.Equal(DiscardReason.SteadyTone, Classify(sustainedTone: true));
    }

    [Fact]
    public void PacketDataFallsOutAsWrongBandOrTooFast()
    {
        // APRS measured on air: almost nothing in the voice band, modulating at ~25 Hz.
        Assert.Equal(DiscardReason.OutsideSpeechBand, Classify(speechBandRatio: 0.02, syllableRateHz: 29.6));
        // With voice-band energy present, the syllable rate is what gives it away.
        Assert.Equal(DiscardReason.TooFastForSpeech, Classify(speechBandRatio: 0.5, syllableRateHz: 24));
    }

    [Fact]
    public void UnidentifiedDigitalIsReportedAsSuchRatherThanAsItsSymptoms()
    {
        // Data has no speech band, no syllable rhythm and a flat-looking envelope, so all three audio
        // tests fire on it — and each would report a symptom instead of the cause. Saying "digital,
        // not identified" tells the operator something they can act on.
        Assert.Equal(
            DiscardReason.DigitalNotIdentified,
            Classify(speechBandRatio: 0.02, syllableRateHz: 29.6, mode: DetectedMode.DigitalUnknown));
    }

    [Fact]
    public void OverloadStillOutranksADigitalVerdict()
    {
        // Nothing measured while the ADC was clipping is real, the classifier's level histogram least
        // of all — compression products make their own structure.
        Assert.Equal(
            DiscardReason.FrontEndOverload,
            Classify(overload: true, mode: DetectedMode.DigitalUnknown));
    }

    [Fact]
    public void FlatAudioHasNoSyllableRhythm()
    {
        Assert.Equal(DiscardReason.NoSyllableRhythm, Classify(modulationDepth: 0.1));
    }

    [Fact]
    public void ShortSpeechIsCalledOutSeparatelyFromNotSpeech()
    {
        Assert.Equal(DiscardReason.NotEnoughVoice, Classify(voicedMs: 400));
    }

    [Fact]
    public void UnknownIsTheFallbackNotADefault()
    {
        // Everything passes: whatever rejected this clip, it was not one of the tests above.
        Assert.Equal(DiscardReason.Unknown, Classify());
        Assert.Equal(0, (int)DiscardReason.Unknown);
    }
}
