using SignalScribe.Enums;

namespace SignalScribe.Analysis;

/// <summary>
/// Which kept recordings have been settled as empty and may age out.
///
/// Known channels record everything their squelch opens, and clips that turn out to hold nothing
/// are kept *labelled* rather than deleted — they are the voice audit's evidence and the operator's
/// window into the gate's judgment. But that value has a shelf life: once the audit has had days to
/// see the row and nobody has reviewed the clip, a squelch-tail hiss burst is just disk. So empty
/// clips get the same treatment rejected clips already get — a retention window, then the purge —
/// rather than either being deleted on sight or hoarded forever.
///
/// "Settled as empty" is deliberately narrow. A clip qualifies only when the pipeline finished
/// judging it and found nothing:
/// <list type="bullet">
/// <item>the authoritative worker transcription ran and produced no text, or capture measured under
/// <see cref="MinVoicedMs"/> of voiced audio so it was never worth queueing;</item>
/// <item>no segment carries text — a decoded packet or D-STAR header is content, whatever the
/// voiced count said;</item>
/// <item>the mode is analog or unmeasured. Digital voice and data clips are excluded outright:
/// their speech is real and merely waiting on a vocoder or decoder, and deleting them would forfeit
/// the reprocess-when-models-improve promise that audio-is-ground-truth exists to keep.</item>
/// </list>
/// A double is protected the same way without a special case: it was never transcribed and its
/// voiced count is real, so it fails the settled-as-empty test.
/// </summary>
public static class NoSpeechRetention
{
    /// <summary>
    /// Voiced milliseconds below which a clip is not worth transcribing — the shared threshold
    /// between ingest (queueing), the UI status label, and this purge.
    /// </summary>
    public const int MinVoicedMs = 300;

    public static bool IsPurgeable(DetectedMode mode, bool transcribed, int voicedMs, bool anySegmentHasText)
    {
        if (mode.IsData() || mode.IsDigitalVoice())
        {
            return false;
        }

        return !anySegmentHasText && (transcribed || voicedMs < MinVoicedMs);
    }
}
