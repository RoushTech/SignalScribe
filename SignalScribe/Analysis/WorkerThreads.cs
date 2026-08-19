namespace SignalScribe.Analysis;

/// <summary>
/// Resolves how many CPU threads an inference run may use.
///
/// This is the only real CPU control in the system. Whisper and llama.cpp each default to grabbing
/// every core they can see, and on a small box that starves the capture daemon — which is a
/// realtime consumer of a 6.4 MSPS sample stream and cannot be rescheduled without dropping
/// samples. So the automatic setting deliberately leaves a core free rather than using them all.
/// </summary>
public static class WorkerThreads
{
    /// <summary>Setting value that means "decide automatically".</summary>
    public const int Automatic = 0;

    /// <summary>
    /// Threads to use for an inference run. <paramref name="configured"/> of
    /// <see cref="Automatic"/> reserves one core for capture; anything else is honoured as-is
    /// (clamped to the machine) so an operator can hand more or less to the workers deliberately.
    /// </summary>
    public static int Resolve(int configured, int processorCount)
    {
        if (processorCount < 1)
        {
            processorCount = 1;
        }

        return configured <= Automatic
            ? Math.Max(1, processorCount - 1)
            : Math.Clamp(configured, 1, processorCount);
    }

    /// <inheritdoc cref="Resolve(int, int)"/>
    public static int Resolve(int configured) => Resolve(configured, Environment.ProcessorCount);

    /// <summary>
    /// Threads per lane when running transcriptions automatically. Whisper's thread scaling is well
    /// past its knee by here: measured, one job on seven threads took the same ~6.3 s per span as it
    /// does on three, because the cost is dominated by a fixed 30-second window rather than by
    /// arithmetic that parallelises. Running several jobs side by side at this width converts that
    /// wasted scaling into throughput.
    /// </summary>
    public const int AutomaticThreadsPerLane = 3;

    /// <summary>
    /// How many transcriptions to run at once, and how wide each one may be.
    ///
    /// One core is held back for the capture daemon in both branches — it is a realtime consumer of
    /// a 6.4 MSPS stream and cannot be rescheduled without dropping samples, which is a correctness
    /// problem rather than a slow one. An operator who sets <paramref name="configured"/> explicitly
    /// gets exactly that width per lane, and as many lanes as the remaining cores allow.
    /// </summary>
    public static (int Lanes, int ThreadsPerLane) PlanLanes(int configured, int processorCount)
    {
        if (processorCount < 1)
        {
            processorCount = 1;
        }

        var threads = configured <= Automatic
            ? Math.Min(AutomaticThreadsPerLane, Math.Max(1, processorCount - 1))
            : Math.Clamp(configured, 1, processorCount);

        return (Math.Max(1, (processorCount - 1) / threads), threads);
    }

    /// <inheritdoc cref="PlanLanes(int, int)"/>
    public static (int Lanes, int ThreadsPerLane) PlanLanes(int configured) =>
        PlanLanes(configured, Environment.ProcessorCount);
}
