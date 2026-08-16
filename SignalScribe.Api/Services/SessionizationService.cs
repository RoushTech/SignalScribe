using Microsoft.EntityFrameworkCore;
using SignalScribe.Data;
using SignalScribe.Data.Models;
using SignalScribe.Enums;

namespace SignalScribe.Api.Services;

/// <summary>
/// Deterministic session clustering and net classification (milestone 5) — pure code over the
/// database, runs in the single-writer host:
///  - transmissions cluster into sessions per channel (gap ≤ 90 s joins; longer starts a new session)
///  - a session overlapping a declared net window on its channel is that net's occurrence (no heuristics)
///  - a closed session (quiet ≥ 3 min) with transcripts gets a Summarize job — nets always, ragchews if long enough
/// Recurrence mining (Source=Mined nets) is the milestone-5b follow-up.
/// </summary>
public sealed class SessionizationService(IServiceScopeFactory scopeFactory, ILogger<SessionizationService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan JoinGap = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan CloseQuiet = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan MinRagchewForSummary = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SignalScribeContext>();
                await AssignSessionsAsync(db, stoppingToken);
                await EnqueueSummariesAsync(db, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sessionization pass failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task AssignSessionsAsync(SignalScribeContext db, CancellationToken ct)
    {
        var unassigned = await db.Transmissions
            .Where(t => t.SessionId == null && t.EndUtc != null)
            .OrderBy(t => t.ChannelId)
            .ThenBy(t => t.StartUtc)
            .Take(500)
            .ToListAsync(ct);

        foreach (var group in unassigned.GroupBy(t => t.ChannelId))
        {
            Session? current = null;
            foreach (var t in group)
            {
                current ??= await db.Sessions
                    .Where(s => s.ChannelId == t.ChannelId)
                    .OrderByDescending(s => s.EndUtc)
                    .FirstOrDefaultAsync(s => s.EndUtc != null && s.EndUtc >= t.StartUtc - JoinGap, ct);

                if (current is null || t.StartUtc - current.EndUtc > JoinGap)
                {
                    current = new Session
                    {
                        ChannelId = t.ChannelId,
                        StartUtc = t.StartUtc,
                        EndUtc = t.EndUtc,
                    };
                    db.Sessions.Add(current);
                    await ClassifyAsync(db, current, ct);
                }

                t.Session = current;
                if (t.EndUtc > current.EndUtc)
                {
                    current.EndUtc = t.EndUtc;
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Declared-window classification: session start inside a net's UTC weekly window ⇒ that net's occurrence.</summary>
    private static async Task ClassifyAsync(SignalScribeContext db, Session session, CancellationToken ct)
    {
        var nets = await db.Nets
            .Where(n => n.ChannelId == session.ChannelId && n.DayOfWeekUtc != null && n.StartTimeUtc != null)
            .ToListAsync(ct);

        foreach (var net in nets)
        {
            var duration = TimeSpan.FromMinutes(net.DurationMinutes ?? 60);
            var start = session.StartUtc;
            if (start.DayOfWeek != net.DayOfWeekUtc)
            {
                continue;
            }

            var windowStart = start.Date + net.StartTimeUtc!.Value.ToTimeSpan() - TimeSpan.FromMinutes(10); // early check-ins
            var windowEnd = windowStart + duration + TimeSpan.FromMinutes(20);
            if (start >= windowStart && start <= windowEnd)
            {
                session.IsNet = true;
                session.NetId = net.Id;
                return;
            }
        }
    }

    private async Task EnqueueSummariesAsync(SignalScribeContext db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - CloseQuiet;
        var candidates = await db.Sessions
            .Where(s => s.Summary == null && s.EndUtc != null && s.EndUtc < cutoff)
            .Where(s => s.Transmissions.Any(t => t.Segments.Any(seg => seg.Transcript != null && seg.Transcript != "")))
            .Take(20)
            .ToListAsync(ct);

        foreach (var session in candidates)
        {
            if (!session.IsNet && session.EndUtc - session.StartUtc < MinRagchewForSummary)
            {
                continue;
            }

            var payload = $$"""{"sessionId":{{session.Id}}}""";
            var alreadyQueued = await db.Jobs.AnyAsync(
                j => j.Type == JobType.Summarize
                    && j.PayloadJson == payload
                    && (j.Status == JobStatus.Pending || j.Status == JobStatus.Leased),
                ct);
            if (!alreadyQueued)
            {
                db.Jobs.Add(new Job { Type = JobType.Summarize, PayloadJson = payload, CreatedUtc = DateTime.UtcNow });
                logger.LogInformation("Queued summary for session {Id} ({Kind})", session.Id, session.IsNet ? "net" : "ragchew");
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
