using SignalScribe.Enums;

namespace SignalScribe.Analysis;

/// <summary>
/// Turns the capture-side audio measurements into the single reason a clip was thrown away, so the
/// operator reviewing discards sees why rather than a wall of numbers. Pure function: the order of
/// the tests is the order of confidence, most definite first.
/// </summary>
public static class DiscardClassifier
{
    /// <summary>Below this much signal-present time a gate opened on a click, not a transmission.</summary>
    public const double MinPresentMs = 200;

    public static DiscardReason Classify(
        bool overload,
        double presentMs,
        bool sustainedTone,
        double speechBandRatio,
        double syllableRateHz,
        int voicedMs,
        double modulationDepth)
    {
        if (overload)
        {
            return DiscardReason.FrontEndOverload;
        }

        if (presentMs < MinPresentMs)
        {
            return DiscardReason.TooShort;
        }

        if (sustainedTone)
        {
            return DiscardReason.SteadyTone;
        }

        if (speechBandRatio < 0.3)
        {
            return DiscardReason.OutsideSpeechBand;
        }

        if (syllableRateHz > 12)
        {
            return DiscardReason.TooFastForSpeech;
        }

        if (modulationDepth <= 0.30)
        {
            return DiscardReason.NoSyllableRhythm;
        }

        return voicedMs < 800 ? DiscardReason.NotEnoughVoice : DiscardReason.Unknown;
    }
}
