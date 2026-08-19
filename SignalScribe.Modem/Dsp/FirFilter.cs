namespace SignalScribe.Modem.Dsp;

/// <summary>
/// Direct-form FIR filter with a circular history buffer.  Coefficient
/// factories generate windowed-sinc (lowpass/bandpass) and windowed-quadrature
/// (correlator) tap sets.
/// </summary>
public sealed class FirFilter
{
    private readonly float[] _taps;
    private readonly float[] _history;
    private int _position;

    public FirFilter(float[] taps)
    {
        _taps = taps;
        _history = new float[taps.Length];
    }

    public int Length => _taps.Length;

    /// <summary>
    /// Pushes one sample through the filter and returns the filtered output.
    /// </summary>
    public float Process(float sample)
    {
        _history[_position] = sample;

        var acc = 0f;
        var idx = _position;
        for (var i = 0; i < _taps.Length; i++)
        {
            acc += _taps[i] * _history[idx];
            idx = idx == 0 ? _history.Length - 1 : idx - 1;
        }

        _position = (_position + 1) % _history.Length;
        return acc;
    }

    public void Reset()
    {
        Array.Clear(_history);
        _position = 0;
    }

    // ── Coefficient factories ─────────────────────────────────────────────────

    /// <summary>
    /// Windowed-sinc lowpass filter taps (Blackman window), unity DC gain.
    /// </summary>
    public static float[] LowpassTaps(int sampleRate, float cutoffHz, int tapCount)
    {
        var taps = new float[tapCount];
        var fc = cutoffHz / sampleRate;
        var center = (tapCount - 1) / 2.0;

        var sum = 0.0;
        for (var i = 0; i < tapCount; i++)
        {
            var x = i - center;
            var sinc = x == 0 ? 2 * Math.PI * fc : Math.Sin(2 * Math.PI * fc * x) / x;
            var w = Blackman(i, tapCount);
            taps[i] = (float)(sinc * w);
            sum += taps[i];
        }

        // Normalise to unity gain at DC.
        for (var i = 0; i < tapCount; i++)
            taps[i] = (float)(taps[i] / sum);

        return taps;
    }

    /// <summary>
    /// Windowed-sinc bandpass filter taps (Blackman window), built as the
    /// difference of two lowpass responses and normalised to unity gain at the
    /// band centre.
    /// </summary>
    public static float[] BandpassTaps(int sampleRate, float lowHz, float highHz, int tapCount)
    {
        var low = LowpassTaps(sampleRate, lowHz, tapCount);
        var high = LowpassTaps(sampleRate, highHz, tapCount);

        var taps = new float[tapCount];
        for (var i = 0; i < tapCount; i++)
            taps[i] = high[i] - low[i];

        // Normalise gain at the geometric band centre.
        var centerHz = Math.Sqrt((double)lowHz * highHz);
        var gain = GainAt(taps, centerHz / sampleRate);
        if (gain > 1e-9)
            for (var i = 0; i < tapCount; i++)
                taps[i] = (float)(taps[i] / gain);

        return taps;
    }

    /// <summary>
    /// Windowed quadrature (sine or cosine) taps at <paramref name="toneHz"/> —
    /// one arm of a tone correlator / matched filter.
    /// </summary>
    public static float[] QuadratureTaps(int sampleRate, float toneHz, int tapCount, bool cosine)
    {
        var taps = new float[tapCount];
        var center = (tapCount - 1) / 2.0;

        for (var i = 0; i < tapCount; i++)
        {
            var angle = 2 * Math.PI * toneHz * (i - center) / sampleRate;
            var w = Blackman(i, tapCount);
            taps[i] = (float)((cosine ? Math.Cos(angle) : Math.Sin(angle)) * w / tapCount);
        }

        return taps;
    }

    private static double Blackman(int i, int tapCount)
    {
        var t = (double)i / (tapCount - 1);
        return 0.42 - 0.5 * Math.Cos(2 * Math.PI * t) + 0.08 * Math.Cos(4 * Math.PI * t);
    }

    private static double GainAt(float[] taps, double normalizedFreq)
    {
        double re = 0, im = 0;
        for (var i = 0; i < taps.Length; i++)
        {
            var angle = 2 * Math.PI * normalizedFreq * i;
            re += taps[i] * Math.Cos(angle);
            im += taps[i] * Math.Sin(angle);
        }
        return Math.Sqrt(re * re + im * im);
    }
}
