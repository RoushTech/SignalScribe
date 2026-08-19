namespace SignalScribe.Analysis;

/// <summary>
/// How long to keep accumulating jobs before spending a Whisper run on them.
///
/// Whisper pads every run to a 30-second mel window, so a run costs the same whether it carries one
/// second of audio or thirty — measured on air, ~7 s either way. <see cref="SpeechPacker"/> already
/// packs as many spans as will fit into one window, but it can only pack what it is given, and the
/// poller hands it whatever happens to be queued at that instant. On sparse traffic that is one
/// clip: a full run spent on a couple of seconds of audio, with the window better than 90% empty.
///
/// Waiting a little changes that. Ham traffic is conversational — overs arrive in bursts, seconds
/// apart — so a short gather turns five separate runs into one, cutting the cost per transmission
/// several-fold and paying for a larger model out of the savings.
///
/// The tuning is a straight latency-for-CPU trade and belongs to the operator, with two rules that
/// keep it from being a bad deal:
/// <list type="bullet">
/// <item>Waiting is only worth it while there is a realistic chance of company. Once enough audio is
/// gathered to fill a window, waiting longer buys nothing and is stopped.</item>
/// <item>A quiet band must not hold a clip forever. The deadline is absolute from the first job, so
/// worst-case latency is bounded and knowable rather than depending on when the next over lands.</item>
/// </list>
/// </summary>
public static class BatchGather
{
    /// <summary>
    /// Audio that fills a window well enough that waiting for more is pointless. Slightly under
    /// <see cref="SpeechPacker.MaxWindowMs"/>: past this the next span starts a second window
    /// anyway, so the run being waited for is no longer shared.
    /// </summary>
    public const int TargetMs = 24_000;

    /// <summary>
    /// Whether to keep waiting for more work before running.
    /// </summary>
    /// <param name="gatheredMs">Voiced audio accumulated so far across the claimed jobs.</param>
    /// <param name="waitedMs">Time since the first job of this batch was claimed.</param>
    /// <param name="budgetMs">Operator's latency budget; zero disables gathering entirely.</param>
    public static bool ShouldKeepGathering(int gatheredMs, int waitedMs, int budgetMs) =>
        budgetMs > 0 && gatheredMs < TargetMs && waitedMs < budgetMs;

    /// <summary>
    /// Runs that a given amount of audio needs, as packed. Used to report the saving honestly:
    /// without gathering each clip is its own run, with it they share.
    /// </summary>
    public static int RunsFor(IReadOnlyList<int> spanLengthsMs) => SpeechPacker.Plan(spanLengthsMs).Count;
}
