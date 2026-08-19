using Microsoft.EntityFrameworkCore;
using SignalScribe.Analysis;
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

        // Nets for the channels in play, loaded once: the occurrence a transmission belongs to has
        // to be known per transmission, and querying that per row would be a round trip each time.
        var channelIds = unassigned.Select(t => t.ChannelId).Distinct().ToList();
        var netsByChannel = (await db.Nets
                .Where(n => channelIds.Contains(n.ChannelId) && n.StartTimeUtc != null)
                .ToListAsync(ct))
            // Weekly before daily, so a channel carrying both resolves to the more specific.
            .OrderBy(n => n.DayOfWeekUtc == null)
            .GroupBy(n => n.ChannelId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var group in unassigned.GroupBy(t => t.ChannelId))
        {
            Session? current = null;
            netsByChannel.TryGetValue(group.Key, out var nets);

            foreach (var t in group)
            {
                var occurrence = FindOccurrence(nets, t.StartUtc);

                // A candidate must be a session this transmission can *extend* — one that already
                // started. Without that guard a back-filled transmission attaches to whatever
                // session ran most recently, because the gap to a later session is negative and
                // passes any "within the join gap" test. See SessionContinuity.
                current ??= await db.Sessions
                    .Where(s => s.ChannelId == t.ChannelId && s.EndUtc != null && s.StartUtc <= t.StartUtc)
                    .OrderByDescending(s => s.EndUtc)
                    .FirstOrDefaultAsync(
                        s => s.EndUtc >= t.StartUtc - JoinGap
                            // Or the session already covering this net occurrence, however long
                            // the lull has been.
                            || (occurrence != null && s.NetId == occurrence.Net.Id
                                && s.StartUtc >= occurrence.Start && s.StartUtc <= occurrence.End),
                        ct);

                // Inside a declared net window the window is the boundary, not the gap: a net is a
                // conversation with long pauses, and splitting on them turns one occurrence into
                // dozens of fragments — each of which would then be summarised on its own.
                var continuesOccurrence = current is not null
                    && occurrence is not null
                    && current.NetId == occurrence.Net.Id
                    && current.StartUtc >= occurrence.Start
                    && current.StartUtc <= occurrence.End;

                if (current is null
                    || (!continuesOccurrence
                        && !SessionContinuity.CanAbsorb(current.StartUtc, current.EndUtc!.Value, t.StartUtc, JoinGap)))
                {
                    current = new Session
                    {
                        ChannelId = t.ChannelId,
                        StartUtc = t.StartUtc,
                        EndUtc = t.EndUtc,
                        IsNet = occurrence is not null,
                        NetId = occurrence?.Net.Id,
                    };
                    db.Sessions.Add(current);
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

    /// <summary>Which net occurrence a moment falls in, if any — the window, not merely the net.</summary>
    private sealed record Occurrence(Net Net, DateTime Start, DateTime End);

    private static Occurrence? FindOccurrence(List<Net>? nets, DateTime atUtc)
    {
        if (nets is null)
        {
            return null;
        }

        foreach (var net in nets)
        {
            if (NetSchedule.TryGetWindow(net.DayOfWeekUtc, net.StartTimeUtc!.Value, net.DurationMinutes, atUtc, out var start, out var end))
            {
                return new Occurrence(net, start, end);
            }
        }

        return null;
    }

    private async Task EnqueueSummariesAsync(SignalScribeContext db, CancellationToken ct)
    {
        // Eligibility is decided inside the query — see SummaryCandidates. Filtering after a capped
        // fetch is what stopped this from ever queueing anything: the page filled with short
        // ragchews that were all discarded, and because they never gained a summary the same
        // rejected page came back every pass while the net occurrences behind it starved.
        var candidates = await SummaryCandidates.FetchAsync(db, DateTime.UtcNow - CloseQuiet, take: 20, ct);

        foreach (var session in candidates)
        {
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
