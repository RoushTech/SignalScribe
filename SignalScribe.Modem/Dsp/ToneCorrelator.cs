namespace SignalScribe.Modem.Dsp;

/// <summary>
/// Quadrature tone detector: correlates the input against windowed sine and
/// cosine references at the tone frequency and outputs the envelope magnitude.
/// Insensitive to input phase, which makes it robust for AFSK where the
/// incoming tone phase is arbitrary.
/// </summary>
public sealed class ToneCorrelator
{
    private readonly FirFilter _inPhase;
    private readonly FirFilter _quadrature;

    public ToneCorrelator(int sampleRate, float toneHz, int tapCount)
    {
        _inPhase = new FirFilter(FirFilter.QuadratureTaps(sampleRate, toneHz, tapCount, cosine: true));
        _quadrature = new FirFilter(FirFilter.QuadratureTaps(sampleRate, toneHz, tapCount, cosine: false));
    }

    /// <summary>
    /// Pushes one sample and returns the current envelope magnitude for the tone.
    /// </summary>
    public float Process(float sample)
    {
        var i = _inPhase.Process(sample);
        var q = _quadrature.Process(sample);
        return MathF.Sqrt(i * i + q * q);
    }

    public void Reset()
    {
        _inPhase.Reset();
        _quadrature.Reset();
    }
}
