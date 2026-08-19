using System.Diagnostics;
using SignalScribe.Enums;
using SignalScribe.Workers.Handlers;
using SignalScribe.Workers.HostApi;
using SignalScribe.Workers.Settings;

namespace SignalScribe.Workers;

// Snapshot updates below feed the StatusReporter's SignalR stream.

/// <summary>Claims jobs from the host queue and dispatches to the matching handler. Lease-based; completion is idempotent.</summary>
public sealed class JobPollerService(
    JobsClient jobs,
    IEnumerable<IJobHandler> handlers,
    WorkerSettingsProvider settings,
    ILogger<JobPollerService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var handlerMap = handlers.ToDictionary(h => h.Type);
        var types = handlerMap.Keys.ToArray();
        logger.LogInformation("Worker {WorkerId} handling: {Types}", _workerId, string.Join(", ", types));

        await settings.RefreshAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (settings.Current.Paused)
            {
                StatusReporter.Snapshot = StatusReporter.Snapshot with { State = "paused", CurrentJob = null };
                await Task.Delay(IdleDelay, stoppingToken);
                continue;
            }

            List<Contracts.ClaimedJob> claimed;
            try
            {
                claimed = await jobs.ClaimAsync(_workerId, types, settings.Current.MaxJobsPerClaim, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Host unreachable ({Message}) — retrying", ex.Message);
                await Task.Delay(IdleDelay, stoppingToken);
                continue;
            }

            if (claimed.Count == 0)
            {
                StatusReporter.Snapshot = StatusReporter.Snapshot with { State = "idle", CurrentJob = null };
                await Task.Delay(IdleDelay, stoppingToken);
                continue;
            }

            claimed = await GatherAsync(claimed, handlerMap, types, stoppingToken);

            // A batch-capable handler gets its type's whole claim in one call and isolates failures
            // itself — transcription packs short clips into shared Whisper windows and runs them in
            // lanes internally. Everything else stays strictly serial: an LLM summary genuinely
            // uses the cores it is given, so two at once would only make both slower.
            foreach (var group in claimed.GroupBy(j => j.Type))
            {
                if (handlerMap[group.Key] is IBatchJobHandler batch)
                {
                    var batchJobs = group.ToList();
                    SetSnapshot(s => s with { State = "running", CurrentJob = $"{group.Key} ×{batchJobs.Count}" });

                    IReadOnlyDictionary<long, string?> outcomes;
                    try
                    {
                        outcomes = await batch.ExecuteBatchAsync(batchJobs, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw; // lease expiry hands the whole claim to the next one
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Batch of {Count} {Type} job(s) failed outright", batchJobs.Count, group.Key);
                        outcomes = batchJobs.ToDictionary(j => j.Id, _ => (string?)ex.Message);
                    }

                    foreach (var job in batchJobs)
                    {
                        // A job the batch never reported on did not complete — fail it so it retries.
                        var error = outcomes.TryGetValue(job.Id, out var e) ? e : "batch returned no outcome";
                        if (error is not null)
                        {
                            logger.LogError("Job {JobId} ({Type}) failed: {Error}", job.Id, job.Type, error);
                        }

                        await jobs.CompleteAsync(job.Id, _workerId, success: error is null, error, stoppingToken);
                        if (error is null)
                        {
                            SetSnapshot(s => s with { JobsCompleted = s.JobsCompleted + 1 });
                        }
                    }

                    continue;
                }

                foreach (var job in group)
                {
                    await RunAsync(job, stoppingToken);
                }
            }
        }

        async ValueTask RunAsync(Contracts.ClaimedJob job, CancellationToken ct)
        {
            SetSnapshot(s => s with { State = "running", CurrentJob = $"{job.Type} #{job.Id}" });
            try
            {
                await handlerMap[job.Type].ExecuteAsync(job, ct);
                await jobs.CompleteAsync(job.Id, _workerId, success: true, error: null, ct);
                SetSnapshot(s => s with { JobsCompleted = s.JobsCompleted + 1 });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // lease expiry hands the job to the next claim
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {JobId} ({Type}) failed", job.Id, job.Type);
                await jobs.CompleteAsync(job.Id, _workerId, success: false, error: ex.Message, ct);
            }
        }
    }

    /// <summary>
    /// Keeps claiming while there is room left in a Whisper window and time left in the operator's
    /// latency budget.
    ///
    /// A run costs the same whether it carries one second of audio or thirty, so running a lone clip
    /// the instant it arrives spends a whole run on a nearly empty window — measured on air, most
    /// runs carried under five seconds of audio into a twenty-seven second window. Ham traffic comes
    /// in conversational bursts, so a short wait usually collects several overs and turns their runs
    /// into one.
    ///
    /// Only batch-capable work is worth gathering: a summary genuinely uses the cores it is given,
    /// so delaying one buys nothing.
    /// </summary>
    private async Task<List<Contracts.ClaimedJob>> GatherAsync(
        List<Contracts.ClaimedJob> claimed,
        IReadOnlyDictionary<JobType, IJobHandler> handlerMap,
        JobType[] types,
        CancellationToken ct)
    {
        var budgetMs = settings.Current.TranscriptionGatherSeconds * 1000;
        if (budgetMs <= 0 || !claimed.Any(j => handlerMap[j.Type] is IBatchJobHandler))
        {
            return claimed;
        }

        var started = Stopwatch.StartNew();
        var gathered = VoicedMs(claimed);
        var initial = claimed.Count;

        while (SignalScribe.Analysis.BatchGather.ShouldKeepGathering(gathered, (int)started.ElapsedMilliseconds, budgetMs))
        {
            SetSnapshot(s => s with { State = "gathering", CurrentJob = $"{claimed.Count} clip(s), {gathered / 1000.0:F0}s audio" });
            await Task.Delay(GatherPoll, ct);

            List<Contracts.ClaimedJob> more;
            try
            {
                more = await jobs.ClaimAsync(_workerId, types, settings.Current.MaxJobsPerClaim, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                break; // host hiccup mid-gather: run what we already hold rather than lose it
            }

            if (more.Count > 0)
            {
                claimed.AddRange(more);
                gathered = VoicedMs(claimed);
            }
        }

        if (claimed.Count > initial)
        {
            logger.LogInformation(
                "Gathered {Total} clip(s) ({Audio:F0}s audio) over {Waited:F0}s — was {Initial}",
                claimed.Count, gathered / 1000.0, started.Elapsed.TotalSeconds, initial);
        }

        return claimed;
    }

    /// <summary>
    /// Voiced audio across a claim, read from the job payloads. Jobs queued before the length was
    /// carried — and the operator's reprocess-everything path — simply contribute nothing, which
    /// makes the gather end on its deadline rather than on a wrong estimate.
    /// </summary>
    private static int VoicedMs(IEnumerable<Contracts.ClaimedJob> claimed)
    {
        var total = 0;
        foreach (var job in claimed)
        {
            try
            {
                if (System.Text.Json.JsonDocument.Parse(job.PayloadJson).RootElement
                    .TryGetProperty("voicedMs", out var voiced))
                {
                    total += voiced.GetInt32();
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A payload we cannot read is one we cannot size; the deadline still bounds the wait.
            }
        }

        return total;
    }

    private static readonly TimeSpan GatherPoll = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Read-modify-write of the shared status snapshot under a lock. Lanes complete concurrently, and
    /// a bare <c>Snapshot = Snapshot with { ... }</c> would drop completions on the floor.
    /// </summary>
    private static void SetSnapshot(Func<WorkerStatusSnapshot, WorkerStatusSnapshot> update)
    {
        lock (SnapshotLock)
        {
            StatusReporter.Snapshot = update(StatusReporter.Snapshot);
        }
    }

    private static readonly Lock SnapshotLock = new();
}
