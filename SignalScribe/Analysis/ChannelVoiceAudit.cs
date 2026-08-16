namespace SignalScribe.Analysis;

/// <summary>
/// Decides whether a channel has earned its place in the capture daemon's known set.
///
/// Known channels record everything their squelch opens — that is deliberate, so a repeater's
/// short overs and courtesy tones are never clipped by a voice test. But a channel is only born
/// from one voice-like burst, and a data frequency can produce one by accident: APRS at 144.390
/// slipped through once and then recorded 1072 packets, because being "known" is permanent.
///
/// This makes it revocable. A channel that has produced plenty of finished recordings and never
/// one word of speech stops bypassing the voice gate; activity there must then look like voice,
/// exactly as it must on any unknown frequency. The first real transcript re-enables it.
/// </summary>
public static class ChannelVoiceAudit
{
    /// <summary>Finished recordings required before a verdict — enough to be sure, small enough to act within an hour of packet traffic.</summary>
    public const int MinResolvedRecordings = 25;

    /// <summary>
    /// Returns the reason to auto-disable, or null to leave the channel enabled.
    /// <paramref name="resolvedCount"/> counts only recordings that have been *settled* — either
    /// transcribed, or never voiced enough to be worth transcribing. Recordings still sitting in
    /// the queue prove nothing yet, so a transcription backlog can never disable a live channel.
    /// </summary>
    public static string? DisableReason(int resolvedCount, int speechCount, DateTime? lastSpeechUtc)
    {
        if (speechCount > 0 || lastSpeechUtc is not null)
        {
            return null;
        }

        return resolvedCount >= MinResolvedRecordings
            ? $"{resolvedCount} recordings, no speech in any of them — likely a data or beacon frequency"
            : null;
    }
}
