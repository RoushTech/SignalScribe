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

            foreach (var job in claimed)
            {
                StatusReporter.Snapshot = StatusReporter.Snapshot with
                {
                    State = "running",
                    CurrentJob = $"{job.Type} #{job.Id}",
                };
                try
                {
                    await handlerMap[job.Type].ExecuteAsync(job, stoppingToken);
                    await jobs.CompleteAsync(job.Id, _workerId, success: true, error: null, stoppingToken);
                    StatusReporter.Snapshot = StatusReporter.Snapshot with
                    {
                        JobsCompleted = StatusReporter.Snapshot.JobsCompleted + 1,
                    };
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw; // lease expiry hands the job to the next claim
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Job {JobId} ({Type}) failed", job.Id, job.Type);
                    await jobs.CompleteAsync(job.Id, _workerId, success: false, error: ex.Message, stoppingToken);
                }
            }
        }
    }
}
