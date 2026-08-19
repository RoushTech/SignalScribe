namespace SignalScribe.Capture.Dsp;

/// <summary>
/// Recovers symbol timing from a discriminator stream, so a framer sees one value per transmitted
/// symbol instead of a waveform.
///
/// The channel rate is not a whole number of samples per symbol — 25 kSPS against 4800 baud is
/// 5.208 — and the transmitter's clock is its own, so the sampling instant has to be interpolated and
/// then tracked. A Gardner detector does both without needing to know what was sent, which matters
/// because it has to lock during a preamble and stay locked through voice payload that looks like
/// noise. It is also blind to symbol *values*, so the same loop serves two-level D-STAR and
/// four-level C4FM unchanged.
///
/// Timing is all this does. It deliberately does not slice, normalise or interpret: those differ per
/// mode, and keeping them out is what lets one synchroniser feed every framer.
/// </summary>
public sealed class SymbolSynchronizer
{
    /// <summary>
    /// Loop gains. Deliberately sluggish: the error estimate from a single symbol is noisy, and a
    /// fast loop chases that noise into jitter that closes the eye it is supposed to open. These
    /// pull in within a few tens of symbols, comfortably inside D-STAR's 64-bit bit-sync preamble and
    /// DMR's sync burst.
    ///
    /// The ratio between them is what matters. A second-order loop is critically damped near
    /// Ki ≈ Kp²/4; run the integrator harder than that and it overshoots and wanders, leaving the
    /// proportional term in a permanent tug-of-war with it. That wander is invisible in a two-level
    /// eye, where the decision is only a sign, and costs 7% of four-level symbols, where it is not.
    /// </summary>
    private const double PhaseGain = 0.02, RateGain = 0.0001;

    /// <summary>Rate correction is bounded to a few hundred ppm — far beyond any real crystal, and enough to stop a bad lock running away.</summary>
    private const double MaxRateAdjust = 0.02;

    /// <summary>
    /// No acquisition boost. It is the obvious idea — run the loop fast to catch the preamble, then
    /// settle — and it was measured to make things worse either way it was applied: boosting the
    /// integrator sent the rate estimate past 2000 ppm and cost five of the nine carrier offsets that
    /// previously recovered a D-STAR header, and boosting only the proportional term still lost one.
    /// The loop pulls in fast enough as it stands; what it does not tolerate is being hurried.
    /// </summary>

    private readonly double _nominalIncrement;

    private double _increment;

    private double _rateAdjust;

    private double _phase;

    // Cubic interpolation needs two samples either side of the instant, so the window lags the input
    // by one sample. Oldest first.
    private float _h0, _h1, _h2, _h3;

    private int _filled;

    // Gardner works on two samples per symbol: the decision instant and the transition midway to it.
    private bool _atCentre;

    private double _centre, _previousCentre, _midpoint;

    public SymbolSynchronizer(double sampleRate, double baud)
    {
        // Two output instants per symbol — the centre and the midpoint before it.
        _nominalIncrement = 2 * baud / sampleRate;
        _increment = _nominalIncrement;
    }

    /// <summary>Samples per symbol the loop has settled on, for diagnostics.</summary>
    public double SamplesPerSymbol => 2 / _increment;

    /// <summary>How far the recovered clock sits from nominal, in parts per million.</summary>
    public double ClockErrorPpm => _rateAdjust * 1e6;

    /// <summary>
    /// Feeds one input sample. Returns true when a symbol instant fell on or before it, with the
    /// interpolated value in <paramref name="symbol"/>.
    /// </summary>
    public bool Feed(float sample, out double symbol)
    {
        symbol = 0;

        _h0 = _h1;
        _h1 = _h2;
        _h2 = _h3;
        _h3 = sample;

        if (_filled < 4)
        {
            _filled++;
            return false;
        }

        _phase += _increment;
        if (_phase < 1)
        {
            return false;
        }

        _phase -= 1;

        // Where between the two middle samples the instant fell. The accumulator advances by
        // _increment per sample and the crossing happened part-way through that step, so the leftover
        // _phase has to be scaled by the increment to become a fraction of a sample — using it
        // directly biases every instant toward the later sample (with a 0.38 increment, never earlier
        // than 0.62 of the way across) and the rate integrator then spends its life fighting that.
        var fraction = Math.Clamp(1 - (_phase / _increment), 0, 1);
        var value = Cubic(_h0, _h1, _h2, _h3, fraction);

        _atCentre = !_atCentre;
        if (!_atCentre)
        {
            _midpoint = value;
            return false;
        }

        _previousCentre = _centre;
        _centre = value;
        symbol = _centre;

        // Gardner: the midpoint sample carries no timing error when sampling is correct, because the
        // waveform crosses through it symmetrically. Weighted by the direction of the transition it
        // sits in, it says which way the clock is off — and says nothing at all when two consecutive
        // symbols are equal, which is exactly right rather than a defect.
        var error = (_centre - _previousCentre) * _midpoint;

        // Normalise so the loop's behaviour does not depend on the signal's amplitude; a loud station
        // must not be tracked more aggressively than a quiet one.
        var magnitude = (Math.Abs(_centre) + Math.Abs(_previousCentre)) / 2;
        if (magnitude > 1e-9)
        {
            error /= magnitude * magnitude;
        }

        error = Math.Clamp(error, -1, 1);

        _rateAdjust = Math.Clamp(_rateAdjust + (RateGain * error), -MaxRateAdjust, MaxRateAdjust);
        _increment = _nominalIncrement * (1 + _rateAdjust);
        _phase += PhaseGain * error;

        return true;
    }

    /// <summary>
    /// Catmull-Rom cubic through the two samples either side of the instant.
    ///
    /// Linear interpolation is not good enough here. At 25 kSPS a 4800-baud symbol is only 5.2
    /// samples, and a straight line across that much curvature leaves an error that varies with the
    /// sampling phase — which is exactly the quantity the timing detector is trying to measure. The
    /// loop cannot tell that bias from a real clock offset, so its rate integrator drifts to cancel
    /// it: measured, a 100 ppm transmitter read as 800 ppm even while every symbol still decoded.
    /// </summary>
    private static double Cubic(double x0, double x1, double x2, double x3, double mu)
    {
        var c1 = 0.5 * (x2 - x0);
        var c2 = x0 - (2.5 * x1) + (2 * x2) - (0.5 * x3);
        var c3 = (0.5 * (x3 - x0)) + (1.5 * (x1 - x2));
        return ((((c3 * mu) + c2) * mu) + c1) * mu + x1;
    }

    /// <summary>Drops timing state without forgetting the learned clock rate — used between bursts on one channel.</summary>
    public void Resync()
    {
        _phase = 0;
        _atCentre = false;
        _centre = _previousCentre = _midpoint = 0;
    }
}
