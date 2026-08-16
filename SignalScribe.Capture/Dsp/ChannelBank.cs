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

    private readonly double _openDb;

    private readonly double _closeDb;

    private readonly int _hangBlocks;

    private readonly string _audioRoot;

    private readonly DateTime _anchorUtc;

    private readonly Func<long, bool> _isKnownFrequency;

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
        Func<long, bool> isKnownFrequency,
        Action<TransmissionIngest> post,
        ILogger logger,
        double deviationHz = NbfmDemodulator.DefaultDeviationHz,
        long monitorLowHz = 0,
        long monitorHighHz = long.MaxValue,
        Action<DiscardIngest>? postDiscard = null,
        double maxOpenSeconds = DefaultMaxOpenSeconds,
        double clipRolloverSeconds = DefaultClipRolloverSeconds)
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
        _openDb = openDb;
        _closeDb = closeDb;
        _hangBlocks = Math.Max(1, (int)(hangMs / 1000.0 * outputRate / (BlockHopsMask + 1)));
        _audioRoot = audioRoot;
        _anchorUtc = anchorUtc;
        _isKnownFrequency = isKnownFrequency;
        _post = post;
        _logger = logger;
        _blockPower = new double[channelCount];
        _floorDb = new double[channelCount];
        _blocksAboveOpen = new byte[channelCount];
        Array.Fill(_floorDb, -110.0);
    }

    public int OpenGateCount => _active.Count;

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
                _isKnownFrequency(tx.ReportedFrequencyHz)));
        }

        gates.Sort((a, b) => a.FrequencyHz.CompareTo(b.FrequencyHz));
        return gates;
    }

    public void OnHop(ReadOnlySpan<float> channelsInterleaved, long hopIndex)
    {
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

        // First block seeds the floors — a fixed init would open every gate (or none).
        if (!_floorsSeeded)
        {
            _floorsSeeded = true;
            _globalSeeded = true;
            for (var c = 0; c < _channelCount; c++)
            {
                _floorDb[c] = db[c];
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
                _floorDb[c] += 0.3 * (db[c] - _floorDb[c]);
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
            var floor = _floorDb[c];
            _floorDb[c] = db[c] < floor ? floor + 0.2 * (db[c] - floor) : floor + 0.005 * (db[c] - floor);

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
                _floorDb[c] = db[c];
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
            _floorDb[bin] = db[bin];
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
        var startUtc = _anchorUtc.AddSeconds(hopIndex / _outputRate);
        var relPath = Path.Combine(startUtc.ToString("yyyy-MM-dd"), $"{freq}_{startUtc:HHmmss_fff}.ogg");
        try
        {
            var tx = new ActiveTransmission(freq, bin, hopIndex, startUtc, relPath,
                Path.Combine(_audioRoot, relPath), _outputRate, _deviationHz);
            tx.PeakDbfs = powerDb;
            tx.GateOpenedHop = gateOpenedHop ?? hopIndex;
            _active[bin] = tx;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to open clip for {Freq}: {Message}", freq, ex.Message);
        }
    }

    private void Finalize(int bin, long hopIndex)
    {
        if (!_active.Remove(bin, out var tx))
        {
            return;
        }

        try
        {
            var ingest = tx.Finish(hopIndex, out var verdict, out var absPath);
            var known = _isKnownFrequency(tx.FrequencyHz);

            // Signal-present time, not wall clock: a key-up transient on a neighbouring channel opens
            // a gate for a block or two, then hang time holds it open long enough to look like a real
            // (short) transmission. Real transmissions are above squelch for hundreds of blocks.
            var presentMs = tx.AboveBlocks * (BlockHopsMask + 1) * 1000.0 / _outputRate;
            var tooShort = presentMs < 200;

            if (tooShort || (!known && !verdict.VoiceLike))
            {
                // Click/spur, or non-voice on an unknown frequency: no channel is born. The clip is
                // reported (not deleted) so the operator can review the gate's decision; the host
                // purges these on a retention schedule.
                var reason = tooShort ? $"only {presentMs:F0} ms of signal" : "not voice";
                _logger.LogInformation("Discarded {Freq} Hz: {Reason} — {Verdict}", tx.FrequencyHz, reason, verdict);

                if (_postDiscard is null)
                {
                    File.Delete(absPath);
                    return;
                }

                _postDiscard(new DiscardIngest(
                    ingest.FrequencyHz, ingest.StartUtc, ingest.EndUtc, ingest.AudioPath,
                    ingest.PeakDbfs, reason, verdict.VoicedMs, Math.Round(verdict.SpeechBandRatio, 3),
                    Math.Round(verdict.ModulationDepth, 3), Math.Round(verdict.SyllableRateHz, 1),
                    verdict.SustainedTone));
                return;
            }

            _logger.LogInformation(
                "Posting {Freq} Hz ({Ms} ms, peak {Peak} dBFS, {Markers} markers)",
                tx.FrequencyHz, (int)(ingest.EndUtc - ingest.StartUtc).TotalMilliseconds, ingest.PeakDbfs, ingest.Markers.Count);
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

    public ActiveTransmission(long frequencyHz, int bin, long startHop, DateTime startUtc, string relPath, string absPath, double hopRate, double deviationHz)
    {
        FrequencyHz = frequencyHz;
        StartHop = startHop;
        GateOpenedHop = startHop;
        _startHop = startHop;
        _startUtc = startUtc;
        _relPath = relPath;
        _absPath = absPath;
        _hopRate = hopRate;
        _demod = new NbfmDemodulator(hopRate, deviationHz);
        _writer = new OpusClipWriter(absPath);
        _analyzer = new AudioAnalyzer();
        _markers.Add(new MarkerIngest(MarkerType.RfEdgeRise, 0, 1.0));
    }

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

    public TransmissionIngest Finish(long endHopAbsolute, out AudioVerdict verdict, out string absPath)
    {
        FlushPcm();
        _writer.Dispose();
        verdict = _analyzer.Finish();
        absPath = _absPath;

        foreach (var tone in _analyzer.ToneEvents)
        {
            _markers.Add(tone.Sustained
                ? new MarkerIngest(MarkerType.HeterodyneDouble, tone.OffsetMs, 0.7)
                : new MarkerIngest(MarkerType.CourtesyTone, tone.OffsetMs, 0.6));
        }

        var durationMs = ElapsedMs();
        _markers.Add(new MarkerIngest(MarkerType.RfEdgeFall, durationMs, 1.0));

        // Report the true carrier, not the filterbank bin centre: amateur channels sit on a 5 kHz
        // grid that the 12.5 kHz analysis grid does not align with (e.g. 146.790 lands on the
        // 146.7875 bin). The discriminator DC gives us the offset to correct it.
        return new TransmissionIngest(
            ReportedFrequencyHz,
            _startUtc,
            _startUtc.AddMilliseconds(durationMs),
            _relPath,
            Math.Round(PeakDbfs, 1),
            Math.Round(_demod.AverageOffsetHz, 1),
            verdict.SustainedTone,
            _markers,
            verdict.VoicedMs);
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
