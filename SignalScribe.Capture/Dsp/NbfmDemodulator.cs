using SignalScribe.Enums;
using SignalScribe.Modem;

namespace SignalScribe.Capture.Dsp;

/// <summary>A decoded packet and where it sat in the transmission.</summary>
public sealed record TimedPacket(DecodedPacket Packet, int OffsetMs, int DurationMs);

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

    private readonly Biquad _audioHighPass1;

    private readonly Biquad _audioHighPass2;

    /// <summary>Anti-aliasing before the 25 kHz → 16 kHz rate change; see the constructor.</summary>
    private readonly Biquad _audioLowPass1;

    private readonly Biquad _audioLowPass2;

    private readonly SubaudibleDetector _subaudible;

    private readonly ModeClassifier _mode;

    private readonly PacketReceiver? _packet;

    private readonly SymbolSynchronizer? _symbols;

    // No receive low-pass on the symbol path. Adding one is the textbook move — a 4800-baud stream
    // only needs ~2.4 kHz and everything above is noise — but measured end to end it recovered one
    // fewer carrier offset than feeding the discriminator straight in, and never fixed the case it
    // was added for. The channelizer has already band-limited this channel to +/-6.25 kHz, so most of
    // what such a filter would remove is not there to begin with, and its group delay is one more
    // thing for the timing loop to chase.

    private readonly Digital.DStar.DStarFramer? _dstar;

    private readonly Digital.Ysf.YsfFramer? _ysf;

    private readonly Digital.C4fm.C4fmSyncDetector? _c4fm;

    private readonly List<Digital.DStar.DStarHeader> _dstarHeaders = [];

    /// <summary>Headers from every digital framer, in the mode-agnostic shape the host stores.</summary>
    private readonly List<Digital.DecodedHeader> _headers = [];

    private readonly List<TimedPacket> _packets = [];

    /// <summary>Samples seen, for stamping a decoded packet with where in the clip it landed.</summary>
    private long _sampleCount;

    /// <summary>Single-sample buffer so the per-sample feed allocates nothing on the hot path.</summary>
    private readonly float[] _packetScratch = new float[1];

    // Per-sample carrier detection from the FM envelope. The block squelch can never resolve a
    // TDMA cadence — a 10 ms gate block sitting half in a 27.5 ms burst and half in the gap is
    // above threshold on average, so its gap half leaks into every squelch-gated estimate (measured:
    // 415 Hz of systematic carrier-offset error on bursty DMR, enough to fail the classifier's
    // symmetry check). FM is constant-envelope, so |IQ|² collapsing by orders of magnitude *is* the
    // carrier dropping, sample-accurately and ahead of any block boundary.
    private const double EnvelopeFloorShare = 0.25; // −6 dB from the tracked peak, squelch's own margin

    private double _envFast;

    private double _envPeak;

    private readonly double _envAlpha;

    private readonly double _envPeakDecay;

    private double _resamplePos = 1;

    private float _lastAudio;

    // 100 ms block DC for quick-key jump detection.
    private double _blockDcSum;

    private int _blockDcCount;

    private readonly int _blockDcLength;

    private readonly long _settleSamples;

    /// <param name="decodePackets">
    /// Run the AFSK soft TNC on this channel. Off by default so tests and diagnostics that only want
    /// audio do not pay for it; the capture bank turns it on.
    /// </param>
    /// <param name="decodeDigital">
    /// Recover 4800-baud symbols and look for D-STAR headers. Far cheaper than the soft TNC — the
    /// timing loop is a few operations per sample and the Viterbi decoder only runs when a sync
    /// pattern actually matches.
    /// </param>
    public NbfmDemodulator(
        double inputSampleRate,
        double deviationHz = DefaultDeviationHz,
        bool decodePackets = false,
        bool decodeDigital = false)
    {
        _inputRate = inputSampleRate;
        _deviationHz = deviationHz > 0 ? deviationHz : DefaultDeviationHz;
        _deemphasisAlpha = 1f - MathF.Exp((float)(-1.0 / (inputSampleRate * 750e-6))); // 750 µs
        _resampleStep = inputSampleRate / OutputSampleRate;
        _dcSlowAlpha = (float)(1.0 - Math.Exp(-1.0 / (inputSampleRate * 0.2)));       // ~200 ms
        // Every FM receiver high-passes the audio: CTCSS and DCS live below 300 Hz, and 750 us
        // de-emphasis is flat below its 212 Hz corner while rolling voice off above it, so the
        // squelch tone arrives *louder* than the speech (measured: 55-76% of the recorded energy).
        // The detector taps ahead of this filter, since the filter exists to discard what it reads.
        // Two sections (24 dB/octave): one is not enough for the 254.1 Hz tone sitting on the corner.
        _audioHighPass1 = Biquad.HighPass(300, OutputSampleRate);
        _audioHighPass2 = Biquad.HighPass(300, OutputSampleRate);

        // Anti-aliasing ahead of the rate change, at the *input* rate. The channelizer delivers
        // 25 kHz and the clip is written at 16 kHz, so everything above 8 kHz has to be gone before
        // the resampler runs or it folds down: measured, a 9 kHz tone landed at 7 kHz only 1.8 dB
        // down, and 11 kHz landed at 5 kHz. Linear interpolation is far too gentle to be that
        // filter, and the 300 Hz high-pass runs after the rate change where it cannot undo a fold.
        //
        // This costs nothing audible — 7 kHz is well above the 2.7 kHz voice band and above the
        // de-emphasised speech that reaches the clip — while removing the part of the FM noise
        // spectrum that folding draws from, which is the worst of it: discriminator noise rises
        // with frequency, so 8-12.5 kHz is the noisiest region of the baseband and it was being
        // mirrored into the voice band at nearly full amplitude.
        _audioLowPass1 = Biquad.LowPass(7_000, inputSampleRate);
        _audioLowPass2 = Biquad.LowPass(7_000, inputSampleRate);
        _subaudible = new SubaudibleDetector(inputSampleRate);
        _mode = new ModeClassifier(inputSampleRate);

        if (decodePackets)
        {
            // Fed the discriminator, not the 16 kHz audio. Measured either way: a packet decodes from
            // the audio path only while the carrier sits within about 2 kHz of its filterbank bin,
            // and 144.390 sits 2.5 kHz off the 12.5 kHz grid — the worst case the plan allows and
            // exactly where APRS lives. Off the discriminator it decodes at every offset tried, which
            // is unsurprising in hindsight: the audio path exists to make speech pleasant, and
            // de-emphasis alone tilts Bell 202's two tones by about 6 dB.
            _packet = PacketReceiver.CreateStandard((int)inputSampleRate);

            // Stamped where the packet *finished*, since that is when the deframer knows it is real.
            // A 1200-baud frame takes its byte count times 8 over 1200 seconds, so the start is
            // recoverable and the span is honest rather than a zero-length instant.
            _packet.PacketReceived += p => _packets.Add(new TimedPacket(
                p,
                Math.Max(0, (int)((_sampleCount * 1000 / _inputRate) - DurationMs(p))),
                (int)DurationMs(p)));
        }
        if (decodeDigital)
        {
            // 4800 baud is the symbol rate D-STAR, DMR, YSF, P25 and NXDN96 all run at, so one
            // synchroniser will serve the framers that follow this one.
            _symbols = new SymbolSynchronizer(inputSampleRate, 4_800);
            _dstar = new Digital.DStar.DStarFramer();
            _dstar.HeaderDecoded += header =>
            {
                _dstarHeaders.Add(header);
                _headers.Add(Digital.DStar.DStarHeaderFields.Describe(header));
            };
            _c4fm = new Digital.C4fm.C4fmSyncDetector();
            _ysf = new Digital.Ysf.YsfFramer();
        }

        _blockDcLength = (int)(inputSampleRate * 0.1);
        _settleSamples = (long)(inputSampleRate * 0.1);
        _envAlpha = 1.0 - Math.Exp(-1.0 / (inputSampleRate * 0.001)); // ~1 ms: crisp burst edges
        _envPeakDecay = 1.0 - Math.Exp(-1.0 / (inputSampleRate * 2.0)); // ~2 s: outlives any TDMA gap
    }

    /// <summary>CTCSS tone riding under the audio, in Hz, once enough of the over has been heard.</summary>
    public double? CtcssHz => _subaudible.Ctcss();

    /// <summary>DCS code riding under the audio, as the octal number operators quote.</summary>
    public int? DcsCode => _subaudible.Dcs();

    /// <summary>AX.25 packets decoded from this transmission. Empty unless packet decoding was enabled.</summary>
    public IReadOnlyList<TimedPacket> Packets => _packets;

    /// <summary>How long a frame of this size takes on air at 1200 baud, including HDLC framing overhead.</summary>
    private static double DurationMs(DecodedPacket packet) => (packet.Raw.Length + 4) * 8 * 1000.0 / 1200;

    /// <summary>D-STAR headers recovered from this transmission — the calling and called stations, in plain text.</summary>
    public IReadOnlyList<Digital.DStar.DStarHeader> DStarHeaders => _dstarHeaders;

    /// <summary>
    /// Every digital header decoded from this transmission, whatever the mode produced it.
    ///
    /// D-STAR's arrives the moment its CRC passes, because one header opens the transmission and
    /// says everything. Fusion's is assembled at the end instead: its FICH repeats ten times a
    /// second, and what is worth reporting is the transmission as a whole once a decoder variant has
    /// proved itself across several frames.
    /// </summary>
    public IReadOnlyList<Digital.DecodedHeader> DecodedHeaders =>
        _ysf?.ToHeader() is { } fusion ? [.. _headers, fusion] : _headers;

    /// <summary>
    /// What modulation this transmission carries. A successful decode outranks the level histogram:
    /// a CRC-valid AX.25 frame or D-STAR header is proof, where the histogram only offers evidence.
    /// Repeated C4FM sync words (DMR, P25, YSF) sit between the two — no CRC vouches for them, but
    /// two same-mode 48-bit matches in one transmission is not something noise produces, so they
    /// still outrank the histogram (which on principle refuses to name any four-level mode).
    /// </summary>
    public DetectedMode Mode =>
        _packets.Count > 0 ? DetectedMode.Afsk1200
        : _dstarHeaders.Count > 0 ? DetectedMode.DStar
        : _ysf is { Decoded: true } ? DetectedMode.Ysf
        : _c4fm is not null && _c4fm.Named != DetectedMode.Unknown ? _c4fm.Named
        : _dstar is { VoiceFramesSeen: true } ? DetectedMode.DStar
        : _mode.Classify();

    /// <summary>Sync words seen for one four-level mode — surfaced for diagnostics and threshold arguments.</summary>
    public int SyncCount(DetectedMode mode) => _c4fm?.SyncCount(mode) ?? 0;

    /// <summary>
    /// What the Fusion framer saw: frame syncs, CRC-valid FICHs, the decoder variant that settled,
    /// and the frame counters it read. The counters are the evidence for whether this project's
    /// reading of the FICH layout is right — real frames count up and wrap, a misread field does
    /// not — so they are logged rather than kept internal.
    /// </summary>
    public string YsfSummary => _ysf is null
        ? "no Fusion framer"
        : $"{_ysf.SyncCount} sync / {_ysf.FichCount} FICH"
            + (_ysf.SettledVariant is { } v ? $" / variant {v} / FN {_ysf.FrameNumberTrace}" : string.Empty);

    /// <summary>
    /// Writes the raw FICH blocks beside the clip when Fusion frames were found but none decoded.
    ///
    /// That combination — sync yes, CRC no — is the whole remaining problem, and it is the one case
    /// where the signal cannot be studied after the fact: the clip is demodulated audio from which
    /// symbols cannot be recovered. Dumping the slicer's own soft bits turns each failure into a
    /// replayable fixture, so candidate conventions can be tried offline against the exact signal
    /// that defeated the decoder instead of by redeploying and waiting for the band.
    /// </summary>
    public void DumpUndecodedFusion(string clipPath)
    {
        if (_ysf is null || _ysf.Decoded || _ysf.CapturedBlocks.Count == 0)
        {
            return;
        }

        Digital.Ysf.YsfFichDump.Write(clipPath, _ysf.SyncCount, _ysf.CapturedBlocks);
    }

    /// <summary>D-STAR voice-frame syncs seen — the CRC-less evidence trail for marginal signals.</summary>
    public int DStarFrameSyncCount => _dstar?.FrameSyncCount ?? 0;

    /// <summary>Of those, the longest run arriving on the 420 ms cadence — the only ones that can name the mode.</summary>
    public int DStarCadencedFrameSyncCount => _dstar?.CadencedFrameSyncs ?? 0;

    /// <summary>Measurements behind the last <see cref="Mode"/> read — for logging and threshold tuning.</summary>
    public ModeScore ModeScore => _mode.LastScore;

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
            _sampleCount++;

            // Every DC estimate freezes while nothing is there. Below squelch the discriminator is
            // noise averaging to zero, and an estimate that chases it un-learns the carrier — fatal
            // for TDMA: a DMR handheld transmits 27.5 ms in every 60, and a tracker that droops
            // toward zero through each 32.5 ms gap turns a fixed carrier offset into a ±700 Hz
            // sawtooth. On 144.980, 5 kHz off its bin, that smeared four clean symbol levels into an
            // asymmetric hump the classifier could only call analog — and the block-DC jump detector
            // read every burst edge as a quick-key, cutting 1.5 s clips into six spans.
            //
            // The envelope term is what makes the gate sharp enough: SignalPresent is a 10 ms block
            // decision that cannot resolve a TDMA cadence, and its boundary blocks leaked enough gap
            // noise to bias the offset by 415 Hz — past the classifier's symmetry tolerance.
            var envelope = (i * i) + (q * q);
            _envFast += _envAlpha * (envelope - _envFast);
            if (_envFast >= _envPeak)
            {
                _envPeak = _envFast;
            }
            else
            {
                _envPeak += _envPeakDecay * (_envFast - _envPeak);
            }

            if (SignalPresent && _envFast > _envPeak * EnvelopeFloorShare)
            {
                if (!_dcSeeded)
                {
                    _dcSlowHz = freqHz;
                    _dcSeeded = true;
                }
                else
                {
                    _dcSlowHz += _dcSlowAlpha * (freqHz - _dcSlowHz);
                }

                _freqSum += freqHz;
                _freqCount++;

                // Ahead of de-emphasis and the limiter, and carrier-offset removed: the classifier
                // reads where the deviation *sits*, and both of those stages move it. Gated on
                // squelch because the tail is noise averaging to zero, which fills the middle of the
                // histogram and makes four discrete symbol levels look like one hump of speech.
                _mode.Feed((float)(freqHz - _dcSlowHz));

                _blockDcSum += freqHz;
                if (++_blockDcCount >= _blockDcLength)
                {
                    _blockDcHz = _blockDcSum / _blockDcCount;
                    _blockDcSum = 0;
                    _blockDcCount = 0;
                    BlockDcReady = true;
                }
            }

            var audio = (float)((freqHz - _dcSlowHz) / _deviationHz);
            _subaudible.Feed(audio); // before the high-pass: this is the only place the tone exists
            if (_packet is not null)
            {
                _packetScratch[0] = audio;
                _packet.ProcessSamples(_packetScratch); // also before de-emphasis, which tilts Bell 202's tones
            }

            if (_symbols is not null && _symbols.Feed(audio, out var recovered))
            {
                // One synchroniser, several framers: 4800 baud is shared by every mode these watch
                // for, and each does its own DC/level tracking, so the same symbol serves both.
                _dstar!.Feed(recovered);
                _c4fm!.Feed(recovered);
                _ysf!.Feed(recovered);
            }

            _deemphState += _deemphasisAlpha * (audio - _deemphState);

            // Band-limit after the soft limiter, so the harmonics the limiter itself generates are
            // caught too, and before the rate change, which is the only place it can help.
            var limited = _audioLowPass2.Process(_audioLowPass1.Process(SoftLimit(_deemphState)));

            // Linear resample to 16 kHz, then high-pass at the output rate so the coefficients match.
            _resamplePos -= 1;
            while (_resamplePos < 1 && written < pcm.Length)
            {
                var frac = (float)_resamplePos;
                var interpolated = _lastAudio + ((limited - _lastAudio) * Math.Clamp(frac, 0f, 1f));
                pcm[written++] = _audioHighPass2.Process(_audioHighPass1.Process(interpolated));
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
