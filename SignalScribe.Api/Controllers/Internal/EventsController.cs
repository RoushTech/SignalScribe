using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SignalScribe.Api.Controllers.Models;
using SignalScribe.Api.Hubs;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Contracts;
using SignalScribe.Data;
using SignalScribe.Data.Models;
using SignalScribe.Enums;

namespace SignalScribe.Api.Controllers.Internal;

/// <summary>Ingest surface for the capture daemon. Idempotent: the daemon replays its spool journal after host outages.</summary>
[ApiController]
[Route("api/internal/events")]
public class EventsController(SignalScribeContext db, IHubContext<StatusHub> hub) : ControllerBase
{
    /// <summary>Callsign without its SSID: "KD9ABC-7" and "KD9ABC" are the same operator.</summary>
    private static string StripSsid(string address)
    {
        var dash = address.IndexOf('-');
        return (dash < 0 ? address : address[..dash]).TrimEnd('*');
    }

    [HttpPost("transmissions")]
    public async Task<ActionResult<TransmissionIngestResult>> IngestTransmission(TransmissionIngest ingest)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.FrequencyHz == ingest.FrequencyHz);
        if (channel is null)
        {
            channel = new Channel
            {
                FrequencyHz = ingest.FrequencyHz,
                Label = $"{ingest.FrequencyHz / 1_000_000.0:F4} MHz",
            };
            db.Channels.Add(channel);
            await db.SaveChangesAsync();
        }

        // Idempotency: (ChannelId, StartUtc) is unique — a spool replay of the same clip is a no-op.
        var existing = await db.Transmissions
            .FirstOrDefaultAsync(t => t.ChannelId == channel.Id && t.StartUtc == ingest.StartUtc);
        if (existing is not null)
        {
            return Ok(new TransmissionIngestResult(existing.Id, AlreadyExisted: true));
        }

        var transmission = new Transmission
        {
            ChannelId = channel.Id,
            StartUtc = ingest.StartUtc,
            EndUtc = ingest.EndUtc,
            AudioPath = ingest.AudioPath,
            PeakDbfs = ingest.PeakDbfs,
            MeanCarrierOffsetHz = ingest.MeanCarrierOffsetHz,
            IsDouble = ingest.IsDouble,
            VoicedMs = ingest.VoicedMs,
            CtcssHz = ingest.CtcssHz,
            DcsCode = ingest.DcsCode,
            Mode = ingest.Mode,
            Markers = ingest.Markers
                .Select(m => new Marker { Type = m.Type, OffsetMs = m.OffsetMs, Confidence = m.Confidence })
                .ToList(),
        };
        db.Transmissions.Add(transmission);

        // The tone identifies the system behind the frequency — two repeaters often share an output
        // channel and the tone is what tells them apart. Learn it, but never overwrite what the
        // operator configured by hand.
        // The measured mode is learned the same way and for the same reason: it says what the
        // frequency carries, and a change of mode is a different system on it. Only a mode we could
        // actually name is worth recording — "some kind of digital" would overwrite a good answer
        // with an unhelpful one every time a burst arrived too weak to classify.
        var learnedMode = ingest.Mode.IsIdentified() ? ingest.Mode : (DetectedMode?)null;

        if ((ingest.CtcssHz is { } tone && channel.LearnedState?.CtcssToneHz != tone)
            || (ingest.DcsCode is { } dcs && channel.LearnedState?.DcsCode != dcs)
            || (learnedMode is { } mode && channel.LearnedState?.Mode != mode))
        {
            var learned = channel.LearnedState ?? new ChannelLearnedState();
            learned.CtcssToneHz = ingest.CtcssHz ?? learned.CtcssToneHz;
            learned.DcsCode = ingest.DcsCode ?? learned.DcsCode;
            if (learnedMode is { } newMode)
            {
                learned.Mode = newMode;
                learned.ModeUpdatedUtc = DateTime.UtcNow;
            }

            learned.UpdatedUtc = DateTime.UtcNow;
            channel.LearnedState = learned;
        }

        // Decoded packets become segments, so they land in the FTS index and every list view without
        // a parallel set of tables. Deliberately *not* routed through IngestTranscript: that path
        // stamps LastSpeechUtc and clears AutoDisabledReason, and an APRS beacon must never count as
        // someone talking — it would re-enable exactly the data frequencies the audit exists to shut.
        if (ingest.Packets is { Count: > 0 } packets)
        {
            transmission.Segments = [.. packets.Select(p => new Segment
            {
                StartMs = p.OffsetMs,
                EndMs = p.OffsetMs + p.DurationMs,
                Transcript = p.Tnc2,
                TranscriptionModel = Segment.PacketDecoderModel,
                // The sending station's callsign is a fact off the wire, not a guess from a phonetic
                // transcript — strip the SSID so it joins up with callsigns seen on voice.
                Callsign = StripSsid(p.Source),
            })];
        }

        // A decoded digital header is the same kind of thing: identity and routing read straight off
        // the air, with no vocoder and no transcription involved. Stored as a segment so the
        // callsign joins up with the same station heard on analog voice, and marked as decoded data
        // so it never counts as someone having spoken. The summary is what lists and search show;
        // the full field set rides along beside it, because on a mode whose voice we cannot decode
        // the header is the entire content of the transmission.
        if (ingest.DigitalHeaders is { Count: > 0 } headers)
        {
            transmission.Segments = [.. transmission.Segments, .. headers.Select(h => new Segment
            {
                StartMs = h.OffsetMs,
                EndMs = h.OffsetMs,
                Transcript = h.Summary,
                TranscriptionModel = Segment.HeaderModel(h.Mode),
                Callsign = h.Callsign,
                HeaderFields = h.Fields,
            })];
        }

        await db.SaveChangesAsync();

        // Only transcribe clips with actual speech in them. Whisper hallucinates on noise — most
        // visibly by echoing its own initial_prompt back as a transcript — so noise clips (which we
        // still record and keep on known channels) never reach it. Digital voice is excluded for the
        // same practical reason from the other direction: there is speech in there, but no vocoder
        // is wired up yet, so all Whisper can do with DMR buzz is hallucinate — the status surface
        // says "not decoded" rather than pretending a transcript is coming.
        // The channel's understood mode is part of the decision, not just the transmission's own: an
        // APRS burst that failed its CRC reads as analog and would otherwise buy a full Whisper run
        // — and a run costs the same as thirty seconds of speech whatever is in it.
        if (Analysis.TranscriptionGate.ShouldTranscribe(
            ingest.Mode,
            channel.Modulation ?? channel.LearnedState?.Mode,
            ingest.IsDouble,
            ingest.VoicedMs))
        {
            // The voiced length rides along so the worker can tell when it has gathered enough to
            // fill a Whisper window without fetching every transmission first.
            db.Jobs.Add(new Job
            {
                Type = JobType.Transcribe,
                PayloadJson = $$"""{"transmissionId":{{transmission.Id}},"voicedMs":{{ingest.VoicedMs}}}""",
                CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await PushAsync(transmission.Id);
        return Ok(new TransmissionIngestResult(transmission.Id, AlreadyExisted: false));
    }

    /// <summary>Transcription results from the workers. Callsign extraction happens here (deterministic, host-side); an Embed job is enqueued per transmission.</summary>
    [HttpPost("transcripts")]
    public async Task<IActionResult> IngestTranscript(TranscriptIngest ingest)
    {
        var transmission = await db.Transmissions.FindAsync(ingest.TransmissionId);
        if (transmission is null)
        {
            return NotFound();
        }

        transmission.TranscribedByModel = ingest.Model;
        if (ingest.Segments.Count > 0)
        {
            // Proof this frequency carries voice: it keeps its place in the capture daemon's known
            // set, and re-enables it if an earlier no-speech streak had auto-disabled it.
            var channel = await db.Channels.FindAsync(transmission.ChannelId);
            if (channel is not null)
            {
                channel.LastSpeechUtc = DateTime.UtcNow;
                if (channel.AutoDisabledReason is not null)
                {
                    channel.AutoDisabledReason = null;
                    channel.Enabled = true;
                }

                // The same proof demotes a learned digital-voice mode — which nothing else can,
                // since analog FM is not an identified mode and never overwrites the learned one.
                // The transmissions that false label silenced get their transcription back.
                if (channel.LearnedState is { } learned && Analysis.LearnedModeDemotion.TranscriptDisproves(learned.Mode))
                {
                    learned.Mode = null;
                    learned.ModeUpdatedUtc = DateTime.UtcNow;
                    learned.UpdatedUtc = DateTime.UtcNow;
                    channel.LearnedState = learned;
                    await RequeueSilencedTransmissionsAsync(channel.Id);
                }
            }
        }

        // Idempotent: re-running a transcription job replaces prior segments for this model.
        var existing = db.Segments.Where(s => s.TransmissionId == transmission.Id);
        db.Segments.RemoveRange(existing);

        foreach (var seg in ingest.Segments)
        {
            var callsigns = Analysis.PhoneticCallsignNormalizer.ExtractCallsigns(seg.Text);
            db.Segments.Add(new Segment
            {
                TransmissionId = transmission.Id,
                StartMs = seg.StartMs,
                EndMs = seg.EndMs,
                Transcript = seg.Text,
                TranscriptionModel = ingest.Model,
                Callsign = callsigns.Count > 0 ? callsigns[0] : null,
            });
        }

        db.Jobs.Add(new Job
        {
            Type = JobType.Embed,
            PayloadJson = $$"""{"transmissionId":{{transmission.Id}}}""",
            CreatedUtc = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        await PushAsync(transmission.Id);
        return Ok();
    }

    /// <summary>
    /// Re-queues transcription for the channel's recordings that a disproved digital-voice verdict
    /// silenced: untranscribed, voice-like, and never vouched for by a decoded header. Their mode
    /// reverts to Unknown — the verdict that named it has just been shown unreliable on this
    /// channel, and if one really was digital, its transcription simply finds no speech.
    /// </summary>
    private async Task RequeueSilencedTransmissionsAsync(int channelId)
    {
        var candidates = await db.Transmissions
            .Where(t => t.ChannelId == channelId
                && t.TranscribedByModel == null
                && Analysis.LearnedModeDemotion.DigitalVoiceModes.Contains(t.Mode)
                && !t.Segments.Any(s => s.TranscriptionModel == Segment.DStarHeaderModel))
            .ToListAsync();

        foreach (var suspect in candidates.Where(t => Analysis.LearnedModeDemotion.IsSuspect(
            t.Mode, hasDecodedHeader: false, alreadyTranscribed: false, t.VoicedMs, t.IsDouble)))
        {
            suspect.Mode = DetectedMode.Unknown;
            db.Jobs.Add(new Job
            {
                Type = JobType.Transcribe,
                PayloadJson = $$"""{"transmissionId":{{suspect.Id}}}""",
                CreatedUtc = DateTime.UtcNow,
            });
        }
    }

    /// <summary>Rejected clips: recorded for review, not attached to any channel, purged on a schedule.</summary>
    [HttpPost("discards")]
    public async Task<IActionResult> IngestDiscard(DiscardIngest ingest)
    {
        var exists = await db.DiscardedClips.AnyAsync(d => d.FrequencyHz == ingest.FrequencyHz && d.StartUtc == ingest.StartUtc);
        if (!exists)
        {
            db.DiscardedClips.Add(new DiscardedClip
            {
                FrequencyHz = ingest.FrequencyHz,
                StartUtc = ingest.StartUtc,
                EndUtc = ingest.EndUtc,
                AudioPath = ingest.AudioPath,
                PeakDbfs = ingest.PeakDbfs,
                Reason = ingest.Reason,
                VoicedMs = ingest.VoicedMs,
                SpeechBandRatio = ingest.SpeechBandRatio,
                ModulationDepth = ingest.ModulationDepth,
                SyllableRateHz = ingest.SyllableRateHz,
                SustainedTone = ingest.SustainedTone,
                CtcssHz = ingest.CtcssHz,
                DcsCode = ingest.DcsCode,
                Mode = ingest.Mode,
            });
            await db.SaveChangesAsync();
        }

        return Ok();
    }

    /// <summary>Streams the row to browsers so the transmissions list updates live (new clip, then its transcript).</summary>
    private async Task PushAsync(long transmissionId)
    {
        var dto = await db.Transmissions
            .Include(t => t.Channel)
            .Include(t => t.Segments)
            .Where(t => t.Id == transmissionId)
            .Select(t => TransmissionMapper.ToDto(t))
            .FirstOrDefaultAsync();
        if (dto is not null)
        {
            await hub.Clients.All.SendAsync(HubEvents.TransmissionChanged, dto);
        }
    }
}
