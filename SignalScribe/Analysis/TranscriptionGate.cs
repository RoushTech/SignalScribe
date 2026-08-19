using SignalScribe.Enums;

namespace SignalScribe.Analysis;

/// <summary>
/// Whether a recording is worth handing to Whisper, decided in one place so the enqueue and the
/// status the operator reads can never disagree about it.
///
/// The expensive fact behind this: Whisper pads every run to a 30-second mel window, so a run costs
/// the same whether it is given one second of audio or thirty (measured, ~7 s either way). Every
/// clip queued that cannot contain speech is therefore a whole run wasted, not a small fraction of
/// one — which is what makes an undecoded packet burst on an APRS frequency worth excluding rather
/// than shrugging at.
/// </summary>
public static class TranscriptionGate
{
    /// <summary>Voiced audio below which a clip was never worth a run — shared with the purge and the UI.</summary>
    public const int MinVoicedMs = NoSpeechRetention.MinVoicedMs;

    /// <summary>
    /// Whether to transcribe, given the transmission's own measured mode and what the channel is
    /// understood to carry.
    ///
    /// The channel term is the addition worth explaining. A CRC-valid packet already suppresses
    /// transcription through <paramref name="mode"/>, but a *failed* decode does not: a collided or
    /// weak burst on 144.390 reads as `AnalogFm`, clears the voiced-audio bar on its own hiss, and
    /// gets a full Whisper run that can only hallucinate. Measured over one evening, 34 of 72 posted
    /// transmissions on that frequency fell through that way. On a channel understood to carry data,
    /// a burst that did not decode is a failed decode — not someone talking.
    ///
    /// <para>This is deliberately keyed on the channel's *understood* mode — the operator's pinned
    /// <c>Modulation</c> first, then the learned one — so it is revocable. A learned label that is
    /// wrong would otherwise silence a voice channel permanently, exactly the trap
    /// <see cref="LearnedModeDemotion"/> exists to undo, and the operator pinning the modulation is
    /// the escape hatch.</para>
    /// </summary>
    public static bool ShouldTranscribe(DetectedMode mode, DetectedMode? channelMode, bool isDouble, int voicedMs)
    {
        if (isDouble || voicedMs < MinVoicedMs)
        {
            return false;
        }

        // Decoded data and digital voice both have their content read (or not) by something other
        // than Whisper; handing either to it produces hallucination, never a transcript.
        if (mode.IsData() || mode.IsDigitalVoice())
        {
            return false;
        }

        return channelMode?.IsData() != true;
    }

    /// <summary>
    /// Whether a clip with no transcript is silently *waiting* for one, or is finished. Without
    /// this, suppressing a data channel's undecoded bursts would leave them reading "processing"
    /// forever — a pending state that never resolves is worse than the wasted run it replaced.
    /// </summary>
    public static bool IsAwaitingTranscription(DetectedMode mode, DetectedMode? channelMode, bool isDouble, int voicedMs, bool alreadyTranscribed) =>
        !alreadyTranscribed && ShouldTranscribe(mode, channelMode, isDouble, voicedMs);
}
