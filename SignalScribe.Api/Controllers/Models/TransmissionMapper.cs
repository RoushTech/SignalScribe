using SignalScribe.Analysis;
using SignalScribe.Data.Models;
using SignalScribe.Enums;

namespace SignalScribe.Api.Controllers.Models;

/// <summary>One mapping used by both the REST list and the live hub push, so a row never changes shape mid-flight.</summary>
public static class TransmissionMapper
{
    /// <summary>Transcription threshold — mirrors the capture-side voice measurement gate in EventsController.</summary>
    public const int MinVoicedMs = Analysis.NoSpeechRetention.MinVoicedMs;

    public static TransmissionDto ToDto(Transmission t) => new(
        t.Id,
        t.ChannelId,
        t.Channel.FrequencyHz,
        t.Channel.Label,
        t.StartUtc,
        t.EndUtc,
        t.IsDouble,
        t.AudioPath,
        Status(t),
        t.CtcssHz,
        t.DcsCode,
        t.Channel.CtcssToneHz ?? t.Channel.LearnedState?.CtcssToneHz,
        t.Channel.LearnedState?.DcsCode,
        t.Mode,
        t.Channel.Modulation ?? t.Channel.LearnedState?.Mode,
        t.Segments
            .OrderBy(s => s.StartMs)
            .Select(s => new SegmentDto(
                s.Id, s.StartMs, s.EndMs, s.Transcript, s.Callsign, s.SpeakerId,
                s.TranscriptionModel == Segment.PacketDecoderModel ? AprsSummary.Describe(s.Transcript) : null,
                s.HeaderFields))
            .ToList());

    private static string Status(Transmission t)
    {
        if (t.IsDouble)
        {
            return "double";
        }

        // Data before transcripts: a decoded packet *is* stored as a segment with text, so the
        // transcript test below would otherwise report a beacon as "transcribed", which reads as
        // someone having spoken.
        //
        // The channel's understood mode counts too. A burst on a data frequency that failed to
        // decode is still data — it is not transcribed, so calling it anything else would leave it
        // reading as pending forever.
        var channelMode = t.Channel.Modulation ?? t.Channel.LearnedState?.Mode;
        if (t.Mode.IsData() || (channelMode?.IsData() == true && !t.Mode.IsDigitalVoice()))
        {
            return t.Segments.Count > 0 ? "decoded" : "data";
        }

        if (t.Segments.Any(s => !string.IsNullOrWhiteSpace(s.Transcript)))
        {
            return "transcribed";
        }

        // Digital voice cannot be transcribed until a vocoder is wired up, so it is neither
        // "processing" (nothing is coming) nor "no speech" (there very likely was some). Where a
        // header was recovered we do at least know *who* — which is a materially different state from
        // knowing nothing about the transmission at all, and worth saying so.
        if (t.Mode.IsDigitalVoice())
        {
            return t.Segments.Count > 0 ? "identified" : "not decoded";
        }

        // "processing" must mean a run is genuinely coming. Anything the gate declined is finished,
        // whatever the reason, and says so.
        return Analysis.TranscriptionGate.IsAwaitingTranscription(
            t.Mode, channelMode, t.IsDouble, t.VoicedMs, t.TranscribedByModel is not null)
            ? "processing"
            : "no speech"; // never worth a run, or transcribed and nothing was said
    }
}
