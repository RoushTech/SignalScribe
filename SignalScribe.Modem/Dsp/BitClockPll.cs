namespace SignalScribe.Modem.Dsp;

/// <summary>
/// Digital PLL for bit-clock recovery.  A 32-bit phase counter advances by a
/// fixed step per audio sample and wraps once per bit period; the wrap instant
/// is the bit-cell sampling point.  Each observed level transition nudges the
/// counter toward zero (the ideal mid-bit position is half a period after a
/// transition), with stronger correction while searching for lock than while
/// locked so that flag hunting is fast but jitter tolerance is high mid-frame.
/// </summary>
public sealed class BitClockPll
{
    private readonly int _stepPerSample;
    private readonly float _lockedInertia;
    private readonly float _searchingInertia;

    private int _phase;
    private bool _previousLevel;
    private bool _locked;

    public BitClockPll(int sampleRate, float baud, float lockedInertia, float searchingInertia)
    {
        _stepPerSample = (int)Math.Round((double)uint.MaxValue * baud / sampleRate);
        _lockedInertia = lockedInertia;
        _searchingInertia = searchingInertia;
    }

    /// <summary>
    /// Signals whether the demodulator believes it is inside a frame; a locked
    /// PLL resists transition jitter more strongly.
    /// </summary>
    public bool Locked
    {
        get => _locked;
        set => _locked = value;
    }

    /// <summary>
    /// Advances the PLL by one audio sample carrying the comparator
    /// <paramref name="level"/>.  Returns <see langword="true"/> when this
    /// sample is a bit-cell sampling point (the caller should then consume
    /// <paramref name="level"/> as the bit-cell value).
    /// </summary>
    public bool Advance(bool level)
    {
        var previousPhase = _phase;
        _phase = unchecked(_phase + _stepPerSample);

        // Wrap from positive to negative — once per bit period.
        var samplePoint = _phase < 0 && previousPhase >= 0;

        if (level != _previousLevel)
        {
            // Nudge the phase toward zero on every transition.
            _phase = (int)(_phase * (_locked ? _lockedInertia : _searchingInertia));
            _previousLevel = level;
        }

        return samplePoint;
    }

    public void Reset()
    {
        _phase = 0;
        _previousLevel = false;
        _locked = false;
    }
}
