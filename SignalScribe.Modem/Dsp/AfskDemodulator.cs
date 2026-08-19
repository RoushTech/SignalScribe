using SignalScribe.Modem.Hdlc;

namespace SignalScribe.Modem.Dsp;

/// <summary>
/// One complete AFSK receive chain: bandpass pre-filter → mark/space tone
/// correlators → per-tone AGC → comparator → PLL bit-clock recovery → NRZI →
/// HDLC deframing.  Feed it float audio samples; it raises
/// <see cref="FrameDemodulated"/> for every CRC-valid AX.25 frame.
/// </summary>
public sealed class AfskDemodulator
{
    private readonly FirFilter? _preFilter;
    private readonly ToneCorrelator _mark;
    private readonly ToneCorrelator _space;
    private readonly Agc _markAgc;
    private readonly Agc _spaceAgc;
    private readonly FirFilter? _postFilter;
    private readonly BitClockPll _pll;
    private readonly NrziDecoder _nrzi = new();
    private readonly HdlcDeframer _deframer = new();

    private readonly int _dcdHoldSamples;
    private readonly int _dcdFlagPairWindowSamples;
    private long _sampleIndex;
    private long _prevFlagSampleIndex = long.MinValue;
    private long _dcdArmedSampleIndex = long.MinValue;

    /// <summary>Raised with the AX.25 frame bytes (FCS stripped) on each valid decode.</summary>
    public event Action<byte[]>? FrameDemodulated;

    public DemodProfile Profile { get; }

    public AfskDemodulator(DemodProfile profile)
    {
        Profile = profile;

        if (profile.PreFilterTaps > 0)
            _preFilter = new FirFilter(FirFilter.BandpassTaps(
                profile.SampleRate, profile.PreFilterLowHz, profile.PreFilterHighHz, profile.PreFilterTaps));

        _mark = new ToneCorrelator(profile.SampleRate, profile.MarkFreq, profile.CorrelatorTaps);
        _space = new ToneCorrelator(profile.SampleRate, profile.SpaceFreq, profile.CorrelatorTaps);
        _markAgc = new Agc(profile.AgcAttack, profile.AgcDecay);
        _spaceAgc = new Agc(profile.AgcAttack, profile.AgcDecay);

        if (profile.PostFilterTaps > 0)
            _postFilter = new FirFilter(FirFilter.LowpassTaps(
                profile.SampleRate, profile.PostFilterCutoffHz, profile.PostFilterTaps));

        _pll = new BitClockPll(
            profile.SampleRate, profile.Baud, profile.PllLockedInertia, profile.PllSearchingInertia);

        // DCD holds for half a second after arming.
        _dcdHoldSamples = profile.SampleRate / 2;
        // Random noise demodulates into lone 0x7E patterns surprisingly often, so a
        // single flag must not arm DCD (it made "Carrier: detected" show on a dead
        // band). A real transmission opens with a preamble of back-to-back flags, so
        // arm only when two flags land within three flag-widths (24 bit times).
        _dcdFlagPairWindowSamples = (int)(profile.SampleRate * 24 / profile.Baud);
        _deframer.FrameReceived += frame => FrameDemodulated?.Invoke(frame);
        _deframer.FlagDetected += () =>
        {
            if (_sampleIndex - _prevFlagSampleIndex <= _dcdFlagPairWindowSamples)
                _dcdArmedSampleIndex = _sampleIndex;
            _prevFlagSampleIndex = _sampleIndex;
        };
    }

    /// <summary>Valid frames decoded by this demodulator.</summary>
    public long ValidFrameCount => _deframer.ValidFrameCount;

    /// <summary>Frames that failed the FCS/length checks.</summary>
    public long InvalidFrameCount => _deframer.InvalidFrameCount;

    /// <summary>
    /// Data-carrier detect: true while consecutive HDLC flags (a real preamble,
    /// not a lone noise-decoded flag) have been seen recently. The sentinel check
    /// matters: subtracting long.MinValue wraps negative, which made DCD read
    /// "detected" from startup until the first flag ever arrived.
    /// </summary>
    public bool CarrierDetected =>
        _dcdArmedSampleIndex != long.MinValue && _sampleIndex - _dcdArmedSampleIndex < _dcdHoldSamples;

    /// <summary>
    /// Processes a block of mono float samples (any nominal level; AGC adapts).
    /// </summary>
    public void ProcessSamples(ReadOnlySpan<float> samples)
    {
        foreach (var raw in samples)
        {
            var sample = _preFilter?.Process(raw) ?? raw;

            var markNorm = _markAgc.Process(_mark.Process(sample));
            var spaceNorm = _spaceAgc.Process(_space.Process(sample));

            var diff = markNorm - spaceNorm;
            if (_postFilter is not null)
                diff = _postFilter.Process(diff);

            var level = diff > 0;

            if (_pll.Advance(level))
            {
                _deframer.ProcessBit(_nrzi.Decode(level));
                _pll.Locked = _deframer.InFrame;
            }

            _sampleIndex++;
        }
    }

    public void Reset()
    {
        _preFilter?.Reset();
        _mark.Reset();
        _space.Reset();
        _markAgc.Reset();
        _spaceAgc.Reset();
        _postFilter?.Reset();
        _pll.Reset();
        _nrzi.Reset();
        _deframer.Reset();
        _prevFlagSampleIndex = long.MinValue;
        _dcdArmedSampleIndex = long.MinValue;
    }
}
