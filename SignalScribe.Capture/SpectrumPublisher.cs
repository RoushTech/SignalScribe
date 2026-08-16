using System.Collections.Concurrent;
using SignalScribe.Contracts;

namespace SignalScribe.Capture;

/// <summary>Hands spectrum rows from the DSP loop to the StatusReporter's hub connection. Bounded — stale rows drop, never block the pipeline.</summary>
public static class SpectrumPublisher
{
    private const int MaxQueued = 8;

    private static readonly ConcurrentQueue<SpectrumRow> Queue = new();

    public static void Publish(SpectrumRow row)
    {
        Queue.Enqueue(row);
        while (Queue.Count > MaxQueued && Queue.TryDequeue(out _))
        {
        }
    }

    public static bool TryDequeue(out SpectrumRow row) => Queue.TryDequeue(out row!);
}
