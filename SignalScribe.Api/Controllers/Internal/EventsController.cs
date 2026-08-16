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
            Markers = ingest.Markers
                .Select(m => new Marker { Type = m.Type, OffsetMs = m.OffsetMs, Confidence = m.Confidence })
                .ToList(),
        };
        db.Transmissions.Add(transmission);

        // The tone identifies the system behind the frequency — two repeaters often share an output
        // channel and the tone is what tells them apart. Learn it, but never overwrite what the
        // operator configured by hand.
        if ((ingest.CtcssHz is { } tone && channel.LearnedState?.CtcssToneHz != tone)
            || (ingest.DcsCode is { } dcs && channel.LearnedState?.DcsCode != dcs))
        {
            var learned = channel.LearnedState ?? new ChannelLearnedState();
            learned.CtcssToneHz = ingest.CtcssHz ?? learned.CtcssToneHz;
            learned.DcsCode = ingest.DcsCode ?? learned.DcsCode;
            learned.UpdatedUtc = DateTime.UtcNow;
            channel.LearnedState = learned;
        }

        await db.SaveChangesAsync();

        // Only transcribe clips with actual speech in them. Whisper hallucinates on noise — most
        // visibly by echoing its own initial_prompt back as a transcript — so noise clips (which we
        // still record and keep on known channels) never reach it.
        if (!ingest.IsDouble && ingest.VoicedMs >= 300)
        {
            db.Jobs.Add(new Job
            {
                Type = JobType.Transcribe,
                PayloadJson = $$"""{"transmissionId":{{transmission.Id}}}""",
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
