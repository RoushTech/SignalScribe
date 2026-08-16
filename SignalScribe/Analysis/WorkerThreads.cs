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
}
