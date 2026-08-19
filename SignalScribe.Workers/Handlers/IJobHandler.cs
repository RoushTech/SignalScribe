using SignalScribe.Contracts;
using SignalScribe.Enums;

namespace SignalScribe.Workers.Handlers;

public interface IJobHandler
{
    JobType Type { get; }

    /// <summary>Executes the job. Throwing marks the job failed (and retried up to the host's attempt cap).</summary>
    Task ExecuteAsync(ClaimedJob job, CancellationToken ct);
}

/// <summary>
/// A handler that does better when handed the whole claim at once. Transcription is the case in
/// point: Whisper's cost is per 30-second window, not per second of audio, so four one-second
/// clips decoded together cost a quarter of what they cost one at a time — but only the handler
/// can know that, so the poller offers the batch and the handler does the packing.
/// </summary>
public interface IBatchJobHandler : IJobHandler
{
    /// <summary>
    /// Executes every job in the batch, isolating failures: the result maps each job id to null on
    /// success or an error message on failure, so one bad clip cannot fail its whole claim.
    /// </summary>
    Task<IReadOnlyDictionary<long, string?>> ExecuteBatchAsync(IReadOnlyList<ClaimedJob> jobs, CancellationToken ct);
}
