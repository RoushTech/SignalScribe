using Microsoft.Extensions.Logging;
using SignalScribe.Capture.Audio;
using SignalScribe.Contracts;
using SignalScribe.Enums;

namespace SignalScribe.Capture.Dsp;

/// <summary>
/// Consumes channelizer hops: per-channel adaptive squelch over every bin simultaneously, and a
/// full transmission lifecycle for open gates — NBFM demod → Opus clip + audio analysis → markers →
/// voice-gated posting. Squelch gates recording; markers (edges, DC jumps, tones) carry
/// segmentation evidence (CLAUDE.md invariant). Channels are born only from voice (plan.md).
/// </summary>
public sealed class ChannelBank : IChannelizerSink
{
    private const int BlockHopsMask = 255;          // gate decisions every 256 hops ≈ 10.24 ms @ 25 kHz

    private const int MaxOpenChannels = 32;

    /// <summary>
    /// Ceiling on gates running the AFSK soft TNC at once.
    ///
    /// Unlike the tone and mode detectors, this one is not free: measured at ~4.7% of a core per open
    /// gate (`PacketReceiverCostTests`) — two demodulator profiles, each a bandpass FIR, two
    /// quadrature correlators, two AGCs, a lowpass FIR and a PLL, per sample. Running it on all 32
    /// gates would cost about one and a half cores on top of the channelizer's 70%, and capture must
    /// never fall behind the sample stream. Eight keeps the worst case near a third of a core while
    /// comfortably covering the handful of gates that are ever candidates at once.
    /// </summary>
    private const int MaxPacketDecoders = 8;

    /// <summary>
    /// Ceiling on gates recovering 4800-baud symbols and looking for D-STAR headers.
    ///
    /// Measured at ~0.5% of a core per gate — thirty times cheaper than the soft TNC, because the
    /// timing loop is a handful of operations per sample and the Viterbi decoder only runs when a
    /// sync pattern actually matches. It could very nearly run everywhere; the cap exists so that the
    /// worst case (a band-wide level event opening every gate at once) cannot stack with the packet
    /// decoders and the channelizer into more than one core.
    /// </summary>
    private const int MaxDigitalDecoders = 16;

    /// <summary>
    /// How much of each channel's recent past to keep, so an opening gate can begin before the
    /// carrier was noticed rather than after.
    ///
    /// The gate deliberately waits two blocks (~20 ms) for a carrier to persist before opening, which
    /// is what keeps clicks and splatter out. The cost of that caution is that the first fraction of
    /// every transmission is already gone by the time recording starts — the clipped first syllable
    /// on analog, and on D-STAR the entire header, which is over within ~25 ms and carries the
    /// callsigns. 150 ms covers the gate's own latency several times over and costs one sequential
    /// frame copy per hop.
    /// </summary>
    public const double PreRollSeconds = 0.15;

    /// <summary>Hard ceiling on a single transmission. Repeater ragchews are long; noise latches are longer.</summary>
    public const double DefaultMaxOpenSeconds = 180;

    /// <summary>
    /// Quick-keying on a repeater holds the carrier (and the gate) up indefinitely. Roll the clip
    /// over at this length so audio keeps flowing to transcription instead of accumulating in one
    /// endless file; sessionization stitches the pieces back into a single session.
    /// </summary>
    public const double DefaultClipRolloverSeconds = 90;

    private readonly long _maxOpenHops;

    private readonly double _clipRolloverSeconds;

    private readonly long _rolloverHops;

    private readonly int _channelCount;

    private readonly double _outputRate;

    private readonly long _centerHz;

    private readonly Func<int, long> _binFrequency;

    /// <summary>Bin spacing in Hz, for resolving a channel to the bin its energy lands in.</summary>
    private readonly long _spacingHz;

    private readonly double _openDb;

    private readonly double _closeDb;

    private readonly int _hangBlocks;

    private readonly string _audioRoot;

    private readonly DateTime _anchorUtc;

    /// <summary>
    /// The known channel a frequency serves, or null. Never an exact-match lookup: the known set
    /// holds real channel frequencies while the bank asks about bin centres and snapped
    /// measurements, and most known channels sit off the analysis grid — see
    /// <see cref="KnownFrequencyResolver"/>.
    /// </summary>
    private readonly Func<long, long?> _resolveKnownFrequency;

    /// <summary>What a known channel is understood to carry; <see cref="DetectedMode.Unknown"/> when we have no idea. Keyed by the known channel's real frequency, so look up with a resolved frequency, never a bin.</summary>
    private readonly Func<long, DetectedMode> _knownMode;

    /// <summary>Gates currently running the soft TNC, held under <see cref="MaxPacketDecoders"/>.</summary>
    private int _packetDecoders;

    /// <summary>Gates currently recovering digital symbols, held under <see cref="MaxDigitalDecoders"/>.</summary>
    private int _digitalDecoders;

    /// <summary>Ring of whole channelizer frames — the recent past of every channel at once.</summary>
    private readonly float[] _preRoll;

    private readonly int _preRollHops;

    private readonly int _frameLength;

    private int _preRollWrite;

    private int _preRollFilled;

    private readonly Action<TransmissionIngest> _post;

    private readonly Action<DiscardIngest>? _postDiscard;

    private readonly double _deviationHz;

    private readonly ILogger _logger;

    private readonly double[] _sortScratch;

    private readonly bool[] _gateable;

    private readonly int _gateableCount;

    private double _globalDb;

    private bool _globalSeeded;

    private readonly double[] _blockPower;

    private readonly double[] _floorDb;

    /// <summary>Bins whose floor the operator has pinned; the tracker leaves these alone.</summary>
    private readonly bool[] _floorPinned;

    private readonly byte[] _blocksAboveOpen;

    private readonly Dictionary<int, ActiveTransmission> _active = [];

    private readonly List<int> _closing = [];

    private readonly List<int> _rolling = [];

    /// <summary>Bins force-closed this block because they stayed open past the safety valve.</summary>
    private readonly List<int> _latched = [];

    /// <summary>Bins that met the open criteria this block, pending the level-event check.</summary>
    private readonly List<int> _opening = [];

    /// <summary>
    /// More than this many gates meeting the open criteria in one 10 ms block is a level event —
    /// a burst of interference, a pager or paging-adjacent splatter, a preamp switching — not that
    /// many stations keying at the same instant. Observed on air: a ~29 s burst opened twelve gates
    /// between 147.0 and 148.0 MHz simultaneously, all within 1 dB of each other, all discarded as
    /// "not voice" after the fact. The band-median reference does not catch this because the burst
    /// covers a fraction of the span, so the median barely moves.
    /// </summary>
    private const int MaxSimultaneousOpens = 3;

    private bool _floorsSeeded;

    private bool _overloaded;

    private bool _clearingOverload;

    /// <summary>
    /// Front-end overload state, pushed in from the sample source. While set, the ADC is clipping and
    /// every bin carries compression products instead of signal, so no gate may open on what is
    /// measured — this is the ground truth the simultaneous-open heuristic can only guess at, and it
    /// is what a nearby transmitter (your own APRS beacon) trips.
    /// </summary>
    public bool Overloaded
    {
        get => _overloaded;
        set
        {
            if (value && !_overloaded)
            {
                _overloadJustDetected = true;
            }
            else if (_overloaded && !value)
            {
                // Do not re-reference the floors yet: the AGC is still recovering and the levels in
                // this instant are a transient, not the noise floor. Hold gates shut through the
                // guard and re-reference at the end of it, when what we measure is real again.
                _overloadGuardBlocks = OverloadGuardBlocks;
            }

            _overloaded = value;
        }
    }

    /// <summary>
    /// How far back to disown gates when an overload is reported. The API's report lags the physical
    /// clipping — measured on air, seven gates opened at peaks up to -1.9 dBFS in the moments before
    /// the event arrived — so a gate that opened just before the report is part of the same event.
    /// </summary>
    private const double OverloadLookbackSeconds = 0.5;

    /// <summary>
    /// Blocks to keep gates shut after an overload clears. The front end does not recover instantly,
    /// and a gate opened into that recovery latched for 49 s on air.
    /// </summary>
    private const int OverloadGuardBlocks = 50;   // ~0.5 s at 10.24 ms per block

    private bool _overloadJustDetected;

    private int _overloadGuardBlocks;

    /// <summary>Blocks to let the filterbank history fill and floors settle before any gate may open.</summary>
    private const int WarmupBlocks = 24;

    private int _blocksSeen;

    public ChannelBank(
        int channelCount,
        double outputRate,
        long centerHz,
        Func<int, long> binFrequency,
        double openDb,
        double closeDb,
        int hangMs,
        string audioRoot,
        DateTime anchorUtc,
        Func<long, long?> resolveKnownFrequency,
        Action<TransmissionIngest> post,
        ILogger logger,
        double deviationHz = NbfmDemodulator.DefaultDeviationHz,
        long monitorLowHz = 0,
        long monitorHighHz = long.MaxValue,
        Action<DiscardIngest>? postDiscard = null,
        double maxOpenSeconds = DefaultMaxOpenSeconds,
        double clipRolloverSeconds = DefaultClipRolloverSeconds,
        Func<long, DetectedMode>? knownMode = null)
    {
        // Bins outside the monitored range are never gated: they sit beyond the SDR's analog
        // passband where levels collapse to ~-100 dBFS and fluctuate enough to trip squelch.
        _gateable = new bool[channelCount];
        var gateable = 0;
        for (var c = 0; c < channelCount; c++)
        {
            var hz = binFrequency(c);
            _gateable[c] = hz >= monitorLowHz && hz <= monitorHighHz;
            if (_gateable[c])
            {
                gateable++;
            }
        }

        _gateableCount = gateable;
        _postDiscard = postDiscard;
        _deviationHz = deviationHz;
        _sortScratch = new double[Math.Max(1, gateable)];
        _maxOpenHops = (long)(maxOpenSeconds * outputRate);
        _rolloverHops = (long)(clipRolloverSeconds * outputRate);
        _clipRolloverSeconds = clipRolloverSeconds;
        _channelCount = channelCount;
        _outputRate = outputRate;
        _centerHz = centerHz;
        _binFrequency = binFrequency;

        // Derived rather than passed: the bin spacing is already implied by the frequency map, and a
        // second source for it could disagree with the first.
        _spacingHz = channelCount > 1 ? Math.Abs(binFrequency(1) - binFrequency(0)) : 12_500;
        _openDb = openDb;
        _closeDb = closeDb;
        _hangBlocks = Math.Max(1, (int)(hangMs / 1000.0 * outputRate / (BlockHopsMask + 1)));
        _audioRoot = audioRoot;
        _anchorUtc = anchorUtc;
        _resolveKnownFrequency = resolveKnownFrequency;
        _knownMode = knownMode ?? (_ => DetectedMode.Unknown);

        _frameLength = channelCount * 2;
        _preRollHops = Math.Max(1, (int)(outputRate * PreRollSeconds));
        _preRoll = new float[(long)_preRollHops * _frameLength <= int.MaxValue
            ? _preRollHops * _frameLength
            : throw new ArgumentException("Pre-roll buffer would exceed addressable size", nameof(channelCount))];
        _post = post;
        _logger = logger;
        _blockPower = new double[channelCount];
        _floorDb = new double[channelCount];
        _floorPinned = new bool[channelCount];
        _blocksAboveOpen = new byte[channelCount];
        Array.Fill(_floorDb, -110.0);
    }

    public int OpenGateCount => _active.Count;

    /// <summary>
    /// Applies the host's stored squelch references and adaptive flags.
    ///
    /// A floor learned over hours is thrown away by a restart otherwise, and the daemon spends its
    /// first minutes back either deaf or chattering while it relearns from a fixed seed. Seeding
    /// from the host also means an operator pinning a floor takes effect on the next refresh rather
    /// than needing capture bounced.
    /// </summary>
    public void ApplySquelchState(IReadOnlyList<ChannelSquelchInfo> channels)
    {
        // Bins are the unit here, channels are what the host knows, and the two do not line up on a
        // 5 kHz band plan — resolve each channel to the bin whose energy it actually lands in.
        var byBin = new Dictionary<int, ChannelSquelchInfo>();
        for (var c = 0; c < _channelCount; c++)
        {
            if (!_gateable[c])
            {
                continue;
            }

            var binHz = _binFrequency(c);
            foreach (var channel in channels)
            {
                if (Math.Abs(channel.FrequencyHz - binHz) <= _spacingHz / 2)
                {
                    byBin[c] = channel;
                    break;
                }
            }
        }

        foreach (var (bin, channel) in byBin)
        {
            _floorPinned[bin] = !channel.Adaptive;
            if (channel.NoiseFloorDbfs is { } floor)
            {
                // Pinned: the stored value *is* the reference, and nothing may move it.
                //
                // Adaptive: the stored value is only a better starting point than the fixed -110
                // seed, and warm-up refines it from there. It deliberately does not survive
                // warm-up, because a stale floor that is too *low* — a gain setting changed while
                // the daemon was down — has the gate chattering on noise, and the tracker's slow
                // upward correction would take seconds to climb out of it. A floor the operator
                // wants held is what the pin is for.
                if (!channel.Adaptive || !_floorsSeeded)
                {
                    _floorDb[bin] = floor;
                }
            }
        }
    }

    /// <summary>
    /// Current squelch references for the bins that serve known channels, for the host to persist.
    /// Only settled floors are offered: reporting the fixed initial seed would overwrite a real
    /// stored value with a placeholder every time the daemon restarted.
    /// </summary>
    public List<NoiseFloorReport> LearnedFloors()
    {
        var reports = new List<NoiseFloorReport>();
        if (!_floorsSeeded)
        {
            return reports;
        }

        for (var c = 0; c < _channelCount; c++)
        {
            if (!_gateable[c] || _floorPinned[c])
            {
                continue;
            }

            if (_resolveKnownFrequency(_binFrequency(c)) is { } known)
            {
                reports.Add(new NoiseFloorReport(known, Math.Round(_floorDb[c], 1)));
            }
        }

        return reports;
    }

    /// <summary>Snapshot of what capture is recording right now — streamed to the UI.</summary>
    public List<ActiveGate> ActiveGates(long hopIndex)
    {
        var gates = new List<ActiveGate>(_active.Count);
        foreach (var (_, tx) in _active)
        {
            gates.Add(new ActiveGate(
                tx.ReportedFrequencyHz,
                Math.Round((hopIndex - tx.StartHop) / _outputRate, 1),
                Math.Round(tx.PeakDbfs, 1),
                _resolveKnownFrequency(tx.ReportedFrequencyHz) is not null));
        }

        gates.Sort((a, b) => a.FrequencyHz.CompareTo(b.FrequencyHz));
        return gates;
    }

    public void OnHop(ReadOnlySpan<float> channelsInterleaved, long hopIndex)
    {
        // Keep the recent past of every channel, so a gate that opens can start from before the
        // carrier appeared rather than from the moment it was noticed. One sequential copy of the
        // whole frame is cheaper than picking channels apart, and it costs the same whether one gate
        // opens or thirty.
        channelsInterleaved.CopyTo(_preRoll.AsSpan(_preRollWrite * _frameLength, _frameLength));
        _preRollWrite = _preRollWrite + 1 == _preRollHops ? 0 : _preRollWrite + 1;
        _preRollFilled = Math.Min(_preRollFilled + 1, _preRollHops);

        for (var c = 0; c < _channelCount; c++)
        {
            var re = channelsInterleaved[2 * c];
            var im = channelsInterleaved[2 * c + 1];
            _blockPower[c] += re * re + im * im;
        }

        foreach (var (bin, tx) in _active)
        {
            tx.FeedIq(channelsInterleaved[2 * bin], channelsInterleaved[2 * bin + 1]);
        }

        if ((hopIndex & BlockHopsMask) == BlockHopsMask)
        {
            EvaluateGates(hopIndex);
        }
    }

    /// <summary>Force-close everything (pipeline teardown) — clips are finalized and posted, not dropped.</summary>
    public void CloseAll(long hopIndex)
    {
        foreach (var bin in new List<int>(_active.Keys))
        {
            Finalize(bin, hopIndex);
        }
    }

    private void EvaluateGates(long hopIndex)
    {
        var blockLen = BlockHopsMask + 1;
        Span<double> db = stackalloc double[_channelCount];
        for (var c = 0; c < _channelCount; c++)
        {
            db[c] = 10 * Math.Log10(_blockPower[c] / blockLen + 1e-20);
        }

        // Squelch works in *relative* space: each channel's level minus the band median (a robust
        // reference dominated by the many idle channels). The RSP's AGC steps the gain of every
        // channel at once; in absolute space that lifts everything above its floor and latches every
        // gate open (observed: 30+ channels opening on the same millisecond, recording 100 s of
        // noise). Relative levels are invariant to any band-wide gain change.
        _globalDb = Median(db);
        for (var c = 0; c < _channelCount; c++)
        {
            db[c] -= _globalDb;
        }

        if (_overloadJustDetected)
        {
            _overloadJustDetected = false;
            var lookbackHops = (long)(OverloadLookbackSeconds * _outputRate);
            foreach (var bin in _active.Keys.ToList())
            {
                if (hopIndex - _active[bin].GateOpenedHop <= lookbackHops)
                {
                    _active[bin].OverloadArtifact = true;
                    _closing.Add(bin);
                }
            }
        }

        if (_overloadGuardBlocks > 0 && --_overloadGuardBlocks == 0)
        {
            _clearingOverload = true;
        }

        // Coming out of overload the levels in front of us are real again, but every floor was
        // learned against clipped garbage (and every open gate froze its floor at a level that no
        // longer exists). Re-reference them all in one block: without this the channels that opened
        // during the overload sit above a stale floor and stay open long after the desense passes,
        // which is exactly the "they never drop" behaviour on air.
        if (_clearingOverload)
        {
            _clearingOverload = false;
            for (var c = 0; c < _channelCount; c++)
            {
                if (_active.ContainsKey(c))
                {
                    continue; // a transmission already in progress keeps its reference
                }

                // Only ever pull a floor *down* to what is there now. Overload drags floors upward
                // (they adapt toward clipped garbage), and this restores them at once — but if a
                // real transmission happens to be on the air as the guard expires, adopting its
                // level as the floor would blind that channel for as long as it kept talking.
                if (!_floorPinned[c])
                {
                    _floorDb[c] = Math.Min(_floorDb[c], db[c]);
                }
                _blocksAboveOpen[c] = 0;
            }
        }

        // First block seeds the floors — a fixed init would open every gate (or none).
        if (!_floorsSeeded)
        {
            _floorsSeeded = true;
            _globalSeeded = true;
            for (var c = 0; c < _channelCount; c++)
            {
                if (!_floorPinned[c])
                {
                    _floorDb[c] = db[c];
                }
                _blockPower[c] = 0;
            }

            return;
        }

        // Warm-up: the channelizer starts with an empty history, so early blocks ramp from silence
        // and would seed nonsense floors (and open every gate). Adapt floors, open nothing.
        if (_blocksSeen++ < WarmupBlocks)
        {
            for (var c = 0; c < _channelCount; c++)
            {
                if (!_floorPinned[c])
                {
                    _floorDb[c] += 0.3 * (db[c] - _floorDb[c]);
                }
                _blockPower[c] = 0;
            }

            return;
        }

        for (var c = 1; c < _channelCount; c++)
        {
            if (c == _channelCount / 2 || !_gateable[c])
            {
                _blockPower[c] = 0;
                continue; // Nyquist, DC (loop starts at 1), or outside the monitored range
            }

            _blockPower[c] = 0;

            if (_active.TryGetValue(c, out var tx))
            {
                tx.PeakDbfs = Math.Max(tx.PeakDbfs, db[c] + _globalDb); // report absolute level
                if (_overloaded)
                {
                    tx.SawOverload = true;
                }

                var present = db[c] >= _floorDb[c] + _closeDb;
                tx.SignalPresent = present;
                if (!present)
                {
                    if (++tx.HangBlocks >= _hangBlocks)
                    {
                        _closing.Add(c);
                    }
                }
                else
                {
                    tx.HangBlocks = 0;
                    tx.AboveBlocks++; // blocks with the signal genuinely present (hang time excluded)
                }

                // Safety valve: nothing legitimate holds a channel this long. Without it a gate that
                // opened on a level shift can stay open indefinitely, writing a giant noise clip.
                if (hopIndex - tx.GateOpenedHop > _maxOpenHops)
                {
                    _logger.LogWarning(
                        "Force-closing {Freq} Hz after {Sec:F0}s continuously open — re-seeding its floor",
                        tx.FrequencyHz, (hopIndex - tx.GateOpenedHop) / _outputRate);
                    _closing.Add(c);
                    _latched.Add(c);
                }
                else if (hopIndex - tx.StartHop > _rolloverHops)
                {
                    _rolling.Add(c); // still active (quick-keying) — cut the clip and keep recording
                }

                continue;
            }

            // Closed channel: track the noise floor (fast down, slow up so transmissions don't drag it).
            // A pinned channel keeps the reference the operator set and only the gate decision below
            // uses it — the point of pinning is a floor the tracker gets wrong.
            if (!_floorPinned[c])
            {
                var floor = _floorDb[c];
                _floorDb[c] = db[c] < floor ? floor + 0.2 * (db[c] - floor) : floor + 0.005 * (db[c] - floor);
            }

            // Open: threshold over floor, sustained for 2 consecutive blocks (keying clicks and other
            // broadband impulses die in one), + local max vs neighbors (kills adjacent-bin double-capture).
            if (db[c] > _floorDb[c] + _openDb
                && db[c] >= db[Math.Max(1, c - 1)]
                && db[c] >= db[Math.Min(_channelCount - 1, c + 1)])
            {
                if (_blocksAboveOpen[c] < 255)
                {
                    _blocksAboveOpen[c]++;
                }

                if (_blocksAboveOpen[c] >= 2
                    && !_overloaded
                    && _overloadGuardBlocks == 0
                    && _active.Count < MaxOpenChannels
                    && !_active.ContainsKey(c - 1)
                    && !_active.ContainsKey(c + 1))
                {
                    _opening.Add(c); // decided below, once we can see how many fired together
                }
            }
            else
            {
                _blocksAboveOpen[c] = 0;
            }
        }

        if (_opening.Count > MaxSimultaneousOpens)
        {
            // Treat the burst as the new reference rather than as traffic: pulling each floor up to
            // the level that is actually there stops the gates firing for as long as it lasts, and
            // the existing fast-down tracking recovers the real floor within a few blocks after it
            // passes. Nothing is recorded and nothing is posted.
            _logger.LogDebug("Level event: {Count} gates met the open threshold in one block — suppressed", _opening.Count);
            foreach (var c in _opening)
            {
                if (!_floorPinned[c])
                {
                    _floorDb[c] = db[c];
                }
                _blocksAboveOpen[c] = 0;
            }
        }
        else
        {
            foreach (var c in _opening)
            {
                // Re-check adjacency and the ceiling here, not at candidate time: the opens are
                // decided together now, so an earlier one in this same block can rule out its
                // neighbour (adjacent-bin double-capture of one strong signal).
                if (_active.Count < MaxOpenChannels
                    && !_active.ContainsKey(c - 1)
                    && !_active.ContainsKey(c + 1))
                {
                    Open(c, hopIndex, db[c] + _globalDb);
                }
            }
        }

        _opening.Clear();

        _blockPower[0] = 0;
        _blockPower[_channelCount / 2] = 0;

        foreach (var bin in _closing)
        {
            Finalize(bin, hopIndex);
        }

        _closing.Clear();

        foreach (var bin in _rolling)
        {
            var freq = _active.TryGetValue(bin, out var open) ? open.FrequencyHz : 0;
            var level = open?.PeakDbfs ?? -120;
            var gateOpenedHop = open?.GateOpenedHop ?? hopIndex;
            Finalize(bin, hopIndex);
            Open(bin, hopIndex, level, gateOpenedHop); // continue recording the same channel in a fresh clip
            _logger.LogInformation("Rolled over {Freq} Hz after {Sec:F0}s (still active)", freq, _clipRolloverSeconds);
        }

        _rolling.Clear();

        // A gate only hits the safety valve when the channel sits above its floor indefinitely — a
        // birdie, a data carrier, or a floor learned before the channel's own level rose. The floor
        // is frozen while a gate is open, so left alone the channel re-opens on the next block and
        // latches again forever. Re-referencing the floor to the level that is actually there means
        // it takes a genuine rise *above the carrier* to re-open; if the carrier later goes away the
        // fast-down tracking recovers the real floor within a few blocks.
        foreach (var bin in _latched)
        {
            if (!_floorPinned[bin])
            {
                _floorDb[bin] = db[bin];
            }
            _blocksAboveOpen[bin] = 0;
        }

        _latched.Clear();
    }

    /// <summary>Median channel power — a robust band-level reference dominated by the many idle channels.</summary>
    private double Median(ReadOnlySpan<double> db)
    {
        var n = 0;
        for (var c = 0; c < db.Length && n < _sortScratch.Length; c++)
        {
            if (_gateable[c])
            {
                _sortScratch[n++] = db[c];
            }
        }

        if (n == 0)
        {
            return -110;
        }

        Array.Sort(_sortScratch, 0, n);
        return _sortScratch[n / 2];
    }

    private void Open(int bin, long hopIndex, double powerDb, long? gateOpenedHop = null)
    {
        var freq = _binFrequency(bin);

        // A clip rollover is a continuation, not a new transmission: its audio already runs up to
        // this hop, and replaying the pre-roll would repeat a slice of what was just written.
        var preRollHops = gateOpenedHop is null ? _preRollFilled : 0;

        // The recording genuinely starts where the pre-roll starts, so the timestamp and the filename
        // have to say so — otherwise every clip claims to begin later than its own first sample.
        var startHop = hopIndex - preRollHops;
        var startUtc = _anchorUtc.AddSeconds(startHop / _outputRate);
        var relPath = Path.Combine(startUtc.ToString("yyyy-MM-dd"), $"{freq}_{startUtc:HHmmss_fff}.ogg");
        try
        {
            var tx = new ActiveTransmission(freq, bin, startHop, startUtc, relPath,
                Path.Combine(_audioRoot, relPath), _outputRate, _deviationHz,
                ShouldDecodePackets(freq), ShouldDecodeDigital(freq),
                preRollMs: (int)(preRollHops * 1000 / _outputRate));
            tx.PeakDbfs = powerDb;

            // The safety valve and the rollover clock measure from when the *gate* opened, not from
            // where the audio starts — pre-roll must not make a transmission look older than it is.
            tx.GateOpenedHop = gateOpenedHop ?? hopIndex;
            _active[bin] = tx;
            if (tx.DecodingPackets)
            {
                _packetDecoders++;
            }

            if (tx.DecodingDigital)
            {
                _digitalDecoders++;
            }

            ReplayPreRoll(tx, bin, preRollHops);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to open clip for {Freq}: {Message}", freq, ex.Message);
        }
    }

    /// <summary>
    /// Feeds a freshly opened transmission the channel's recent past, oldest first.
    ///
    /// Squelch is reported as *absent* throughout: this audio is from before the gate decided there
    /// was a carrier, so most of it is noise. The measurements that describe the transmission — the
    /// carrier-offset average and the modulation histogram — are gated on squelch and so correctly
    /// ignore it, while the decoders are not, and so get exactly what they were missing.
    /// </summary>
    private void ReplayPreRoll(ActiveTransmission tx, int bin, int hops)
    {
        if (hops <= 0)
        {
            return;
        }

        tx.SignalPresent = false;

        var oldest = ((_preRollWrite - hops) % _preRollHops + _preRollHops) % _preRollHops;
        for (var i = 0; i < hops; i++)
        {
            var slot = (oldest + i) % _preRollHops;
            var offset = (slot * _frameLength) + (2 * bin);
            tx.FeedIq(_preRoll[offset], _preRoll[offset + 1]);
        }

        tx.SignalPresent = true;
    }

    /// <summary>
    /// Whether this gate should run the soft TNC. The rule is to spend it only where it could change
    /// the answer:
    /// <list type="bullet">
    /// <item>An <b>unknown frequency</b> — this is where identification matters most, because a
    /// packet burst is otherwise discarded as not-speech and the frequency stays invisible. A decoded
    /// frame is what turns it into a labelled channel.</item>
    /// <item>A channel already understood to carry <b>data</b> — that is where the packets are, and
    /// once decoding created the channel it must keep decoding it, or the channel would go quiet the
    /// moment it became known.</item>
    /// </list>
    /// A known analog voice repeater is deliberately excluded. That does give up the mismatch case
    /// the tone detector runs everywhere to catch — but the tone detector costs 0.1% of a core and
    /// this costs 4.7%, and the free <see cref="ModeClassifier"/> still watches every gate, so a
    /// repeater that starts carrying data shows up as two-level digital and can be enabled.
    /// </summary>
    private bool ShouldDecodePackets(long frequencyHz)
    {
        if (_packetDecoders >= MaxPacketDecoders)
        {
            return false;
        }

        var known = _resolveKnownFrequency(frequencyHz);
        return known is null || _knownMode(known.Value).IsData();
    }

    /// <summary>
    /// Whether this gate should recover digital symbols. Same reasoning as the soft TNC — spend it
    /// where it can change the answer — but the budget is wider because it is far cheaper: an unknown
    /// frequency, or a channel already understood to carry something digital, whether voice or data.
    /// </summary>
    private bool ShouldDecodeDigital(long frequencyHz)
    {
        if (_digitalDecoders >= MaxDigitalDecoders)
        {
            return false;
        }

        var known = _resolveKnownFrequency(frequencyHz);
        if (known is null)
        {
            return true;
        }

        var mode = _knownMode(known.Value);
        return mode.IsDigitalVoice() || mode.IsData();
    }

    private void Finalize(int bin, long hopIndex)
    {
        if (!_active.Remove(bin, out var tx))
        {
            return;
        }

        if (tx.DecodingPackets)
        {
            _packetDecoders--;
        }

        if (tx.DecodingDigital)
        {
            _digitalDecoders--;
        }

        try
        {
            // Resolve against the snapped measurement, not the raw bin: the known set holds real
            // channel frequencies, and an off-grid channel (147.180 on a 147.175 bin) never
            // exact-matches its own bin. Posting the resolved frequency matters just as much — a
            // clip whose offset never settled reports its bin centre otherwise, and bin centres
            // arriving at the host spring phantom half-grid channels into existence (144.3875
            // collected 29 clips that belonged to 144.390 before auto-disabling itself).
            var knownFrequency = _resolveKnownFrequency(tx.ReportedFrequencyHz);
            var ingest = tx.Finish(hopIndex, out var verdict, out var absPath, knownFrequency);
            var known = knownFrequency is not null;

            // Signal-present time, not wall clock: a key-up transient on a neighbouring channel opens
            // a gate for a block or two, then hang time holds it open long enough to look like a real
            // (short) transmission. Real transmissions are above squelch for hundreds of blocks.
            var presentMs = tx.AboveBlocks * (BlockHopsMask + 1) * 1000.0 / _outputRate;
            var tooShort = presentMs < SignalScribe.Analysis.DiscardClassifier.MinPresentMs;

            // Sounding like voice is no longer the only way to earn a channel: a transmission we can
            // *name* has told us what the frequency is, which is the thing the voice gate was really
            // standing in for. Note this takes a specifically identified mode — "some kind of digital"
            // deliberately does not qualify, or an unidentified packet frequency would start hoarding
            // every burst again (see DetectedModeExtensions.IsIdentified).
            if (tx.OverloadArtifact || tooShort || (!known && !verdict.VoiceLike && !ingest.Mode.IsIdentified()))
            {
                // Click/spur, or unrecognised non-voice on an unknown frequency: no channel is born.
                // The clip is reported (not deleted) so the operator can review the gate's decision;
                // the host purges these on a retention schedule.
                var reason = SignalScribe.Analysis.DiscardClassifier.Classify(
                    tx.OverloadArtifact, presentMs, verdict.HeterodyneTone, verdict.SpeechBandRatio,
                    verdict.SyllableRateHz, verdict.VoicedMs, verdict.ModulationDepth, ingest.Mode);
                _logger.LogInformation(
                    "Discarded {Freq} Hz: {Reason} — {Verdict}; {Mode} {Score}, {Syncs}",
                    tx.FrequencyHz, reason, verdict, ingest.Mode, tx.ModeScore, tx.SyncSummary);

                if (_postDiscard is null)
                {
                    File.Delete(absPath);
                    return;
                }

                _postDiscard(new DiscardIngest(
                    ingest.FrequencyHz, ingest.StartUtc, ingest.EndUtc, ingest.AudioPath,
                    ingest.PeakDbfs, reason, verdict.VoicedMs, Math.Round(verdict.SpeechBandRatio, 3),
                    Math.Round(verdict.ModulationDepth, 3), Math.Round(verdict.SyllableRateHz, 1),
                    verdict.SustainedTone, tx.CtcssHz, tx.DcsCode, ingest.Mode));
                return;
            }

            // Log what was actually posted, with the bin alongside when they differ: an off-grid
            // channel is recorded on one frequency and filed under another, and a log naming only
            // the bin cannot be matched against the channel list at all.
            _logger.LogInformation(
                "Posting {Freq} Hz{Bin} ({Ms} ms, peak {Peak} dBFS, {Mode} {Score}, {Syncs}, {Markers} markers)",
                ingest.FrequencyHz,
                ingest.FrequencyHz == tx.FrequencyHz ? string.Empty : $" (bin {tx.FrequencyHz})",
                (int)(ingest.EndUtc - ingest.StartUtc).TotalMilliseconds, ingest.PeakDbfs, ingest.Mode, tx.ModeScore, tx.SyncSummary, ingest.Markers.Count);
            _post(ingest);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to finalize transmission on {Freq}: {Message}", tx.FrequencyHz, ex.Message);
        }
    }
}

/// <summary>One open squelch gate: demod chain → Opus clip + audio analysis + markers.</summary>
internal sealed class ActiveTransmission
{
    private readonly long _startHop;

    private readonly DateTime _startUtc;

    private readonly string _relPath;

    private readonly string _absPath;

    private readonly double _hopRate;

    private readonly NbfmDemodulator _demod;

    private readonly OpusClipWriter _writer;

    private readonly AudioAnalyzer _analyzer;

    private readonly List<MarkerIngest> _markers = [];

    private readonly float[] _iqPair = new float[2];

    private readonly float[] _audioScratch = new float[8];

    private readonly short[] _pcmStage = new short[1024];

    private int _pcmStageFill;

    private double _lastBlockDcHz = double.NaN;

    private long _lastHop;

    public ActiveTransmission(long frequencyHz, int bin, long startHop, DateTime startUtc, string relPath, string absPath, double hopRate, double deviationHz, bool decodePackets = false, bool decodeDigital = false, int preRollMs = 0)
    {
        FrequencyHz = frequencyHz;
        StartHop = startHop;
        GateOpenedHop = startHop;
        _startHop = startHop;
        _startUtc = startUtc;
        _relPath = relPath;
        _absPath = absPath;
        _hopRate = hopRate;
        DecodingPackets = decodePackets;
        DecodingDigital = decodeDigital;
        _demod = new NbfmDemodulator(hopRate, deviationHz, decodePackets, decodeDigital);
        _writer = new OpusClipWriter(absPath);
        _analyzer = new AudioAnalyzer();
        // The RF edge is where the gate actually opened, which with pre-roll is no longer the start
        // of the clip. Marking it at zero would tell segmentation the carrier appeared during what
        // is really the quiet lead-in.
        _markers.Add(new MarkerIngest(MarkerType.RfEdgeRise, preRollMs, 1.0));
    }

    /// <summary>Whether this gate is running the soft TNC — held against the bank's decoder budget.</summary>
    public bool DecodingPackets { get; }

    /// <summary>Whether this gate is recovering digital symbols — held against the bank's decoder budget.</summary>
    public bool DecodingDigital { get; }

    /// <summary>AX.25 packets decoded from this transmission.</summary>
    public IReadOnlyList<TimedPacket> Packets => _demod.Packets;

    /// <summary>D-STAR headers decoded from this transmission.</summary>
    public IReadOnlyList<Digital.DStar.DStarHeader> DStarHeaders => _demod.DStarHeaders;

    public long FrequencyHz { get; }

    public long StartHop { get; }

    /// <summary>
    /// Hop at which the squelch gate opened, carried across clip rollovers. The max-open safety
    /// valve must measure from here, not from <see cref="StartHop"/>: rolling the clip starts a new
    /// ActiveTransmission, so a valve keyed on StartHop resets every 90 s and never fires — which is
    /// exactly how a latched gate produced ten minutes of chained noise clips.
    /// </summary>
    public long GateOpenedHop { get; set; }

    /// <summary>Best current estimate of the carrier's offset from bin centre.</summary>
    public double CarrierOffsetHz => _demod.AverageOffsetHz;

    /// <summary>True once the offset estimate is trustworthy; until then the UI shows the bin frequency.</summary>
    public bool OffsetSettled => _demod.OffsetSettled;

    /// <summary>True channel frequency: bin centre corrected by the measured offset, snapped to the channel grid.</summary>
    public long ReportedFrequencyHz => OffsetSettled ? ChannelGrid.Snap(FrequencyHz, CarrierOffsetHz) : FrequencyHz;

    /// <summary>Blocks the channel was actually above squelch — distinguishes a transmission from a splatter transient held open by hang time.</summary>
    public int AboveBlocks { get; set; }

    /// <summary>True if the front end overloaded at any point while this clip was recording — the audio will have a hole in it.</summary>
    public bool SawOverload { get; set; }

    /// <summary>True if this gate opened as part of a front-end overload, so the clip is receiver artefact, not signal.</summary>
    public bool OverloadArtifact { get; set; }

    /// <summary>CTCSS tone read from under the audio, if any.</summary>
    /// <summary>Measurements behind the mode verdict — logged with discards so a misread is diagnosable from the field.</summary>
    public ModeScore ModeScore => _demod.ModeScore;

    /// <summary>Sync words seen by the framer path, per mode, as a compact log string.</summary>
    public string SyncSummary =>
        $"{_demod.SyncCount(DetectedMode.Dmr)} DMR / {_demod.SyncCount(DetectedMode.P25Phase1)} P25 / "
        + $"{_demod.SyncCount(DetectedMode.Ysf)} YSF / {_demod.SyncCount(DetectedMode.Nxdn)} NXDN / "
        + $"{_demod.DStarFrameSyncCount} D-STAR fs sync(s), {_demod.DStarCadencedFrameSyncCount} cadenced; "
        + $"Fusion {_demod.YsfSummary}";

    public double? CtcssHz => _demod.CtcssHz;

    /// <summary>DCS code read from under the audio, if any.</summary>
    public int? DcsCode => _demod.DcsCode;

    /// <summary>Squelch state, forwarded to the demodulator so the carrier-offset average ignores the tail.</summary>
    public bool SignalPresent
    {
        set => _demod.SignalPresent = value;
    }

    public double PeakDbfs { get; set; } = -120;

    public int HangBlocks { get; set; }

    public void FeedIq(float i, float q)
    {
        _lastHop++;
        _iqPair[0] = i;
        _iqPair[1] = q;
        var n = _demod.Process(_iqPair, _audioScratch);
        for (var s = 0; s < n; s++)
        {
            // Demod output is soft-limited to ±1, so this scale can never clamp.
            _pcmStage[_pcmStageFill++] = (short)(_audioScratch[s] * 30_000);
            if (_pcmStageFill == _pcmStage.Length)
            {
                FlushPcm();
            }
        }

        if (_demod.BlockDcReady)
        {
            var dc = _demod.TakeBlockDc();
            if (!double.IsNaN(_lastBlockDcHz) && Math.Abs(dc - _lastBlockDcHz) > 350)
            {
                // Quick-key: a different transmitter's carrier offset mid-clip.
                _markers.Add(new MarkerIngest(MarkerType.DcOffsetJump, ElapsedMs(), 0.8));
            }

            _lastBlockDcHz = dc;
        }
    }

    public TransmissionIngest Finish(long endHopAbsolute, out AudioVerdict verdict, out string absPath, long? knownFrequencyHz = null)
    {
        FlushPcm();
        _writer.Dispose();
        verdict = _analyzer.Finish();
        absPath = _absPath;
        _demod.DumpUndecodedFusion(_absPath);

        foreach (var tone in _analyzer.ToneEvents)
        {
            if (tone.ToneHz is < 300 or > 2700)
            {
                continue; // hum or leaked CTCSS: not a courtesy tone, and never an over boundary
            }

            _markers.Add(tone.Sustained
                ? new MarkerIngest(MarkerType.HeterodyneDouble, tone.OffsetMs, 0.7)
                : new MarkerIngest(MarkerType.CourtesyTone, tone.OffsetMs, 0.6));
        }

        var durationMs = ElapsedMs();
        _markers.Add(new MarkerIngest(MarkerType.RfEdgeFall, durationMs, 1.0));

        // Report the true carrier, not the filterbank bin centre: amateur channels sit on a 5 kHz
        // grid that the 12.5 kHz analysis grid does not align with (e.g. 146.790 lands on the
        // 146.7875 bin). The discriminator DC gives us the offset to correct it — and when the bin
        // serves a known channel, that channel's frequency outranks the measurement, so clips whose
        // offset never settled do not post under a bin centre and fragment the channel's history.
        return new TransmissionIngest(
            knownFrequencyHz ?? ReportedFrequencyHz,
            _startUtc,
            _startUtc.AddMilliseconds(durationMs),
            _relPath,
            Math.Round(PeakDbfs, 1),
            Math.Round(_demod.AverageOffsetHz, 1),
            verdict.HeterodyneTone,
            _markers,
            verdict.VoicedMs,
            _demod.CtcssHz,
            _demod.DcsCode,
            _demod.Mode,
            [.. _demod.Packets.Select(p => new PacketIngest(
                p.OffsetMs,
                p.DurationMs,
                p.Packet.Tnc2,
                p.Packet.Frame.Source.ToString(),
                p.Packet.Frame.Destination.ToString()))],
            [.. _demod.DecodedHeaders.Select(h => new DigitalHeaderIngest(
                0,
                h.Mode,
                h.Callsign,
                h.Summary,
                [.. h.Fields]))]);
    }

    private int ElapsedMs() => (int)(_lastHop * 1000 / _hopRate);

    private void FlushPcm()
    {
        if (_pcmStageFill > 0)
        {
            _writer.Write(_pcmStage.AsSpan(0, _pcmStageFill));
            _analyzer.FeedShorts(_pcmStage.AsSpan(0, _pcmStageFill));
            _pcmStageFill = 0;
        }
    }
}
