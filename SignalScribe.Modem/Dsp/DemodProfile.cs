namespace SignalScribe.Modem.Dsp;

/// <summary>
/// Tuning parameters for one <see cref="AfskDemodulator"/> instance.  Multiple
/// profiles with different filter/AGC characteristics can run in parallel on
/// the same audio, with duplicate decodes collapsed by the frame deduper.
/// </summary>
public sealed record DemodProfile
{
    public required string Name { get; init; }
    public required int SampleRate { get; init; }
    public float Baud { get; init; } = 1200f;
    public float MarkFreq { get; init; } = 1200f;
    public float SpaceFreq { get; init; } = 2200f;

    /// <summary>Input bandpass pre-filter; 0 taps disables it.</summary>
    public int PreFilterTaps { get; init; }
    public float PreFilterLowHz { get; init; } = 900f;
    public float PreFilterHighHz { get; init; } = 2500f;

    /// <summary>Tone correlator window length in samples.</summary>
    public required int CorrelatorTaps { get; init; }

    /// <summary>Post-comparator lowpass on the mark−space difference; 0 taps disables it.</summary>
    public int PostFilterTaps { get; init; }
    public float PostFilterCutoffHz { get; init; } = 1440f;

    public float AgcAttack { get; init; } = 0.130f;
    public float AgcDecay { get; init; } = 0.00013f;

    public float PllLockedInertia { get; init; } = 0.74f;
    public float PllSearchingInertia { get; init; } = 0.50f;

    /// <summary>
    /// The standard profile: bandpass pre-filter, ~1.2-symbol correlator
    /// window, per-tone AGC, and a gentle post-filter.  Starting point modelled
    /// on well-known AFSK demodulator practice; tuned via the round-trip tests.
    /// </summary>
    public static DemodProfile Standard(int sampleRate)
    {
        var samplesPerSymbol = sampleRate / 1200f;
        return new DemodProfile
        {
            Name = "A",
            SampleRate = sampleRate,
            PreFilterTaps = (int)(samplesPerSymbol * 1.6f) | 1,
            CorrelatorTaps = (int)(samplesPerSymbol * 1.2f) | 1,
            PostFilterTaps = (int)(samplesPerSymbol * 1.0f) | 1,
        };
    }
}
