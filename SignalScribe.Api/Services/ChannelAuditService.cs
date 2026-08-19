using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Analysis;
using SignalScribe.Api.Hubs;
using SignalScribe.Data;
using SignalScribe.Data.Models;
using SignalScribe.Enums;

namespace SignalScribe.Api.Services;

/// <summary>
/// Revokes the known-channel bypass from frequencies that never carry voice (see
/// <see cref="ChannelVoiceAudit"/>), and drops the transcription work already queued for them.
/// Runs in the web host — the single DB writer — on a short sweep, so a data frequency costs at
/// most one sweep of junk rather than a day of it.
/// </summary>
public sealed class ChannelAuditService(
    IServiceScopeFactory scopeFactory,
    IHubContext<StatusHub> hub,
    ILogger<ChannelAuditService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Channel audit failed");
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }
    }

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SignalScribeContext>();

        var candidates = await db.Channels
            .Where(c => c.Enabled && c.LastSpeechUtc == null)
            .Select(c => new
            {
                Channel = c,
                // "Resolved" = transcription has had its say: either it ran, or the clip never had
                // enough voiced audio to be queued. Pending jobs are excluded on purpose.
                Resolved = c.Transmissions.Count(t => t.TranscribedByModel != null || t.VoicedMs < 300),
                // Decoded packets live in the segment table too, so "has a transcript" is not the
                // same as "someone spoke". A beacon frequency must not talk its way out of the audit.
                Speech = c.Transmissions.Count(t => t.Segments.Any(s =>
                    s.Transcript != null
                    && s.TranscriptionModel != Segment.PacketDecoderModel
                    && s.TranscriptionModel != Segment.DStarHeaderModel)),
            })
            .ToListAsync(ct);

        var disabled = 0;
        foreach (var c in candidates)
        {
            var reason = ChannelVoiceAudit.DisableReason(
                c.Resolved, c.Speech, c.Channel.LastSpeechUtc, c.Channel.LearnedState?.Mode);
            if (reason is null)
            {
                continue;
            }

            c.Channel.Enabled = false;
            c.Channel.AutoDisabledReason = reason;
            disabled++;

            // The queued work for a data frequency is pure waste — it is what buries the queue and
            // starves real traffic of transcription. Drop the pending jobs, keep the recordings.
            var ids = await db.Transmissions
                .Where(t => t.ChannelId == c.Channel.Id)
                .Select(t => t.Id)
                .ToListAsync(ct);
            var mine = ids.ToHashSet();

            var pending = await db.Jobs
                .Where(j => j.Type == JobType.Transcribe && j.CompletedUtc == null)
                .ToListAsync(ct);
            var stale = pending.Where(j => TransmissionIdOf(j.PayloadJson) is { } id && mine.Contains(id)).ToList();
            db.Jobs.RemoveRange(stale);

            logger.LogWarning(
                "Auto-disabled {Label}: {Reason} — dropped {Jobs} queued transcription job(s)",
                c.Channel.Label, reason, stale.Count);
        }

        if (disabled > 0)
        {
            await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync("channelsChanged", ct);
        }

        return disabled;
    }

    private static long? TransmissionIdOf(string payloadJson)
    {
        try
        {
            return System.Text.Json.JsonDocument.Parse(payloadJson)
                .RootElement.GetProperty("transmissionId").GetInt64();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
