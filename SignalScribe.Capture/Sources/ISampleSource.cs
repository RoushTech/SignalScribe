namespace SignalScribe.Capture.Sources;

/// <summary>
/// Wideband complex sample source. The RSP1 source and the IQ-file replay source are interchangeable
/// (CLAUDE.md invariant) so all DSP is regression-testable against recorded fixtures.
/// </summary>
public interface ISampleSource : IDisposable
{
    /// <summary>Sample rate in samples/second (complex).</summary>
    double SampleRate { get; }

    /// <summary>Center frequency in Hz. Park between channels so the zero-IF DC spike lands on no one.</summary>
    long CenterFrequencyHz { get; }

    /// <summary>
    /// Monotonic count of samples delivered since <see cref="Start"/>. All timestamps derive from this
    /// counter anchored to NTP-synced wall clock once per stream — never per-event wall clock reads.
    /// A stalled counter is the SDRPlay-service-wedged signal: tear down and re-init.
    /// </summary>
    long SampleCounter { get; }

    void Start();

    void Stop();

    /// <summary>
    /// Reads up to <paramref name="iq"/>.Length/2 complex samples as interleaved I/Q floats in [-1, 1].
    /// Returns the number of floats written. Blocks until samples are available or the source stops (returns 0).
    /// </summary>
    int Read(Span<float> iq);
}
