using SignalScribe.Contracts;
using SignalScribe.Enums;

namespace SignalScribe.Workers.Handlers;

public interface IJobHandler
{
    JobType Type { get; }

    /// <summary>Executes the job. Throwing marks the job failed (and retried up to the host's attempt cap).</summary>
    Task ExecuteAsync(ClaimedJob job, CancellationToken ct);
}
