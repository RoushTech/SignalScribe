namespace SignalScribe.Capture.Dsp;

/// <summary>
/// Per-transmission NBFM demodulation: quadrature discriminator → DC-offset removal (which also
/// absorbs carrier/grid misalignment) → de-emphasis → linear resample to 16 kHz mono.
///
/// The discriminator's slow DC is the transmitter frequency-error fingerprint (simplex) and its
/// windowed jumps mark quick-key speaker changes — both surfaced to the caller.
/// Zero allocations after construction.
/// </summary>
public sealed class NbfmDemodulator
{
    public const int OutputSampleRate = 16_000;

    /// <summary>Deviation mapped to full scale. Standard amateur NBFM peak deviation is ±5 kHz (±2.5 kHz on narrowband channels).</summary>
    public const double DefaultDeviationHz = 5_000;

    /// <summary>Soft-limiter knee: below this the audio path is exactly linear.</summary>
    private const float LimiterKnee = 0.6f;

    private readonly double _deviationHz;

    private readonly double _inputRate;

    private readonly float _deemphasisAlpha;

    private readonly double _resampleStep;

    private readonly float _dcSlowAlpha;

    private float _prevI;

    private float _prevQ;

    private bool _primed;

    private double _dcSlowHz;

    // Cumulative mean of instantaneous frequency: the best estimator of a stationary carrier's
    // offset, and unlike the EWMA it is unbiased from the first block onward (an EWMA seeded on
    // sample 0 is still crawling toward the truth when a 1-second APRS burst ends).
    private double _freqSum;

    private long _freqCount;

    private bool _dcSeeded;

    private float _deemphState;

    private double _resamplePos = 1;

    private float _lastAudio;

    // 100 ms block DC for quick-key jump detection.
    private double _blockDcSum;

    private int _blockDcCount;

    private readonly int _blockDcLength;

    private readonly long _settleSamples;

    public NbfmDemodulator(double inputSampleRate, double deviationHz = DefaultDeviationHz)
    {
        _inputRate = inputSampleRate;
        _deviationHz = deviationHz > 0 ? deviationHz : DefaultDeviationHz;
        _deemphasisAlpha = 1f - MathF.Exp((float)(-1.0 / (inputSampleRate * 750e-6))); // 750 µs
        _resampleStep = inputSampleRate / OutputSampleRate;
        _dcSlowAlpha = (float)(1.0 - Math.Exp(-1.0 / (inputSampleRate * 0.2)));       // ~200 ms
        _blockDcLength = (int)(inputSampleRate * 0.1);
        _settleSamples = (long)(inputSampleRate * 0.1);
    }

    /// <summary>Slow-tracked discriminator DC in Hz — used for DC removal and the quick-key fingerprint.</summary>
    public double CarrierOffsetHz => _dcSlowHz;

    /// <summary>Cumulative-mean carrier offset — what to use when identifying the true channel frequency.</summary>
    public double AverageOffsetHz => _freqCount > 0 ? _freqSum / _freqCount : 0;

    /// <summary>
    /// Whether the carrier is currently above squelch. The offset average must only accumulate
    /// while it is: the squelch tail and hang time carry no carrier, so the discriminator there is
    /// noise averaging to zero, and including it drags the estimate toward the bin centre in
    /// proportion to how much tail the clip has. A 0.3 s APRS packet inside a 1.4 s clip reported
    /// ~540 Hz instead of 2500 Hz, which is not enough to snap onto the right channel.
    /// </summary>
    public bool SignalPresent { get; set; } = true;

    /// <summary>False until enough samples have accumulated for the offset to be meaningful (~100 ms).</summary>
    public bool OffsetSettled => _freqCount >= _settleSamples;

    /// <summary>Set when a completed 100 ms block's DC is available; consume via TakeBlockDc.</summary>
    public bool BlockDcReady { get; private set; }

    private double _blockDcHz;

    public double TakeBlockDc()
    {
        BlockDcReady = false;
        return _blockDcHz;
    }

    /// <summary>Demodulates channel IQ (interleaved) into 16 kHz audio. Returns samples written to <paramref name="pcm"/> (≤ iq complex count).</summary>
    public int Process(ReadOnlySpan<float> iq, Span<float> pcm)
    {
        var written = 0;
        var freqScale = _inputRate / (2 * Math.PI);
        for (var s = 0; s + 1 < iq.Length; s += 2)
        {
            var i = iq[s];
            var q = iq[s + 1];
            if (!_primed)
            {
                // No phase reference yet — a fabricated first delta would poison the DC tracker.
                _primed = true;
                _prevI = i;
                _prevQ = q;
                continue;
            }

            var cross = _prevI * q - _prevQ * i;
            var dot = _prevI * i + _prevQ * q;
            _prevI = i;
            _prevQ = q;
            var freqHz = Math.Atan2(cross, dot) * freqScale;

            if (!_dcSeeded)
            {
                _dcSlowHz = freqHz;
                _dcSeeded = true;
            }
            else
            {
                _dcSlowHz += _dcSlowAlpha * (freqHz - _dcSlowHz);
            }

            if (SignalPresent)
            {
                _freqSum += freqHz;
                _freqCount++;
            }

            _blockDcSum += freqHz;
            if (++_blockDcCount >= _blockDcLength)
            {
                _blockDcHz = _blockDcSum / _blockDcCount;
                _blockDcSum = 0;
                _blockDcCount = 0;
                BlockDcReady = true;
            }

            var audio = (float)((freqHz - _dcSlowHz) / _deviationHz);
            _deemphState += _deemphasisAlpha * (audio - _deemphState);
            var limited = SoftLimit(_deemphState);

            // Linear resample to 16 kHz.
            _resamplePos -= 1;
            while (_resamplePos < 1 && written < pcm.Length)
            {
                var frac = (float)_resamplePos;
                pcm[written++] = _lastAudio + (limited - _lastAudio) * Math.Clamp(frac, 0f, 1f);
                _resamplePos += _resampleStep;
            }

            _lastAudio = limited;
        }

        return written;
    }

    /// <summary>
    /// Soft knee limiter, output strictly bounded to ±1. Over-deviating stations and FM noise
    /// spikes compress smoothly instead of hard-clipping at the PCM conversion (which is what
    /// "overdriven on loud signals" sounds like). Below the knee the path is bit-for-bit linear.
    /// </summary>
    private static float SoftLimit(float x)
    {
        var magnitude = MathF.Abs(x);
        if (magnitude <= LimiterKnee)
        {
            return x;
        }

        var over = (magnitude - LimiterKnee) / (1f - LimiterKnee);
        var compressed = LimiterKnee + (1f - LimiterKnee) * MathF.Tanh(over);
        return MathF.CopySign(compressed, x);
    }
}
