using SignalScribe.Enums;

namespace SignalScribe.Analysis;

/// <summary>
/// Undoes a falsely learned digital-voice mode on a channel.
///
/// A learned digital-voice mode is expensive when it is wrong, twice over: transmissions carrying
/// that mode are never queued for transcription (no vocoder is wired up, so Whisper would only
/// hallucinate on the buzz), and the channel becomes exempt from the no-speech audit because it is
/// "understood". Worse, the mistake is self-sealing — analog FM is deliberately not an *identified*
/// mode, so ordinary voice traffic can never overwrite the learned mode the way a real mode change
/// would, and the label sticks until something disproves it.
///
/// A transcript is that something. The worker-side VAD is the authoritative voice check, so a
/// transmission on the channel producing actual speech segments is proof the channel carries analog
/// voice; the learned digital-voice mode is demoted, and the channel's untranscribed digital-voice
/// transmissions that never decoded a header become suspects to re-queue — if they really were
/// digital, transcription finds no speech and returns nothing, so a wrong re-queue costs one wasted
/// job while a right one recovers a silenced recording.
///
/// A genuinely mixed-mode repeater (analog and D-STAR on one frequency) demotes on its analog overs
/// and re-learns on its digital ones — the learned mode tracking whichever system spoke last, which
/// is the same behaviour mode learning already has. Transmissions with a decoded header keep their
/// mode: the CRC vouched for those, and no transcript elsewhere on the channel can unsay it.
/// </summary>
public static class LearnedModeDemotion
{
    /// <summary>The modes a learned label can silence transcription for — the ones a transcript can disprove.</summary>
    public static readonly DetectedMode[] DigitalVoiceModes =
        [.. Enum.GetValues<DetectedMode>().Where(m => m.IsDigitalVoice())];

    /// <summary>
    /// Whether a real transcript on the channel disproves its learned mode. Only digital voice is
    /// disprovable this way: data modes never suppressed transcription in the first place, and an
    /// unknown or analog label has nothing to demote.
    /// </summary>
    public static bool TranscriptDisproves(DetectedMode? learnedMode) =>
        learnedMode?.IsDigitalVoice() == true;

    /// <summary>
    /// Whether a transmission was silenced by a header-less digital-voice verdict and deserves a
    /// second chance at transcription once that verdict is disproved. Mirrors the ingest-time gate
    /// (voiced, not a double) so the re-queue only ever admits what would have been queued had the
    /// mode read analog.
    /// </summary>
    public static bool IsSuspect(DetectedMode mode, bool hasDecodedHeader, bool alreadyTranscribed, int voicedMs, bool isDouble) =>
        mode.IsDigitalVoice() && !hasDecodedHeader && !alreadyTranscribed && voicedMs >= 300 && !isDouble;
}
