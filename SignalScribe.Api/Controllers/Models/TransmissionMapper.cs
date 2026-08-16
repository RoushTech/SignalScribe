using SignalScribe.Data.Models;

namespace SignalScribe.Api.Controllers.Models;

/// <summary>One mapping used by both the REST list and the live hub push, so a row never changes shape mid-flight.</summary>
public static class TransmissionMapper
{
    /// <summary>Transcription threshold — mirrors the capture-side voice measurement gate in EventsController.</summary>
    public const int MinVoicedMs = 300;

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
        t.Segments
            .OrderBy(s => s.StartMs)
            .Select(s => new SegmentDto(s.Id, s.StartMs, s.EndMs, s.Transcript, s.Callsign, s.SpeakerId))
            .ToList());

    private static string Status(Transmission t)
    {
        if (t.IsDouble)
        {
            return "double";
        }

        if (t.Segments.Any(s => !string.IsNullOrWhiteSpace(s.Transcript)))
        {
            return "transcribed";
        }

        if (t.VoicedMs < MinVoicedMs || t.TranscribedByModel is not null)
        {
            return "no speech"; // either never worth transcribing, or transcribed and nothing was said
        }

        return "processing";
    }
}
