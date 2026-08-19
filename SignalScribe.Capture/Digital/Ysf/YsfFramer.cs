using SignalScribe.Enums;

namespace SignalScribe.Capture.Digital.Ysf;

/// <summary>
/// Finds Fusion frames in the recovered symbol stream and decodes their FICH.
///
/// A frame is 480 symbols — 20 of frame sync, 100 of FICH, 360 of payload — repeating every 100 ms
/// for as long as the carrier stands. That repetition is what makes this worth doing without a
/// vocoder: the FICH arrives ten times a second, CRC-protected, and says what the transmission is
/// even though its voice needs AMBE we do not have.
///
/// <para><b>Polarity is searched, not assumed</b>, as everywhere else in this project: inverting the
/// discriminator flips the sign of every C4FM symbol, which flips the first bit of every dibit, so
/// the sync is matched against the pattern and its dibit-inverted twin.</para>
///
/// <para><b>The protocol conventions are searched too, and a CRC settles them.</b> See
/// <see cref="YsfFichVariant"/>: several conventions inside the documented decode chain are not
/// published anywhere reachable, each is self-consistent when wrong, and a decoder built on the
/// wrong one is silent rather than incorrect. Rather than guess and redeploy until something
/// decodes, every combination is tried on every frame and the one that produces CRC-valid frames
/// repeatedly is the real one. <see cref="MinFrames"/> repeats are required before any of it is
/// reported, because a 16-bit CRC tried 64 ways will occasionally pass on noise alone.</para>
/// </summary>
public sealed class YsfFramer
{
    /// <summary>The 40-bit frame sync word opening every frame, as 20 C4FM dibits.</summary>
    public const ulong FrameSync = 0xD471C9634DUL;

    private const int SyncBits = 40;

    private const ulong SyncMask = (1UL << SyncBits) - 1;

    /// <summary>
    /// Sync bits allowed to be wrong. Generous on purpose: behind this sits a CRC that a false sync
    /// cannot pass, so the only cost of a wrong match is the Viterbi decodes it triggers, while the
    /// benefit is holding sync on a signal at the edge of copy.
    /// </summary>
    private const int MaxSyncErrors = 4;

    /// <summary>
    /// Inverting the discriminator flips every symbol's sign, which in the C4FM dibit mapping flips
    /// the first bit of every dibit and leaves the second alone — one mask over the sync word.
    /// </summary>
    private const ulong InversionMask = 0xAAAAAAAAAAUL & SyncMask;

    /// <summary>
    /// CRC-valid frames one variant must produce before the transmission is reported as Fusion.
    ///
    /// With 64 variants tried per frame, a 16-bit CRC passes by accident about once in a thousand
    /// frames across the whole set — often enough to see in an evening of traffic. Requiring three
    /// from the *same* variant makes that vanish (a specific variant's accidental rate is one in
    /// 65536 per frame), while real Fusion clears it in under half a second.
    /// </summary>
    public const int MinFrames = 3;

    /// <summary>Time constant for the framer's own DC and level tracking, in symbols — see <see cref="DStar.DStarFramer"/>.</summary>
    private const double TrackingAlpha = 1.0 / 64;

    private double _dc;

    private double _level = 1;

    private long _tracked;

    private ulong _recent;

    private int _seen;

    private bool _collecting;

    private bool _inverted;

    private readonly double[] _fich = new double[YsfFichDecoder.Dibits * 2];

    private int _collected;

    private readonly Dictionary<YsfFichVariant, List<YsfFich>> _byVariant = [];

    /// <summary>
    /// How many raw FICH blocks to keep for offline study.
    ///
    /// This is the difference between iterating on the decoder in minutes and iterating in days.
    /// The recovered symbol stream exists nowhere else: clips are demodulated, de-emphasised,
    /// limited Opus audio, from which symbols cannot be recovered, so a decoder that fails on air
    /// cannot be re-run against the signal that defeated it. Keeping the soft bits exactly as the
    /// slicer produced them turns every failed transmission into a fixture that can be replayed
    /// against any number of candidate conventions without the radio being involved at all.
    /// </summary>
    private const int MaxCapturedBlocks = 64;

    private readonly List<double[]> _captured = [];

    /// <summary>Raw FICH soft-bit blocks as they came off the air, for offline decoding experiments.</summary>
    public IReadOnlyList<double[]> CapturedBlocks => _captured;

    /// <summary>Frame syncs matched, whatever came of them.</summary>
    public int SyncCount { get; private set; }

    /// <summary>FICHs that passed a CRC, across all variants.</summary>
    public int FichCount { get; private set; }

    /// <summary>
    /// The variant that has decoded the most frames, once one has cleared <see cref="MinFrames"/>.
    /// This is the answer to which conventions Fusion actually uses, learned from air rather than
    /// assumed — worth logging, because once it is known it can be pinned and the search dropped.
    /// </summary>
    public YsfFichVariant? SettledVariant
    {
        get
        {
            YsfFichVariant? best = null;
            var most = MinFrames - 1;
            foreach (var (variant, frames) in _byVariant)
            {
                if (frames.Count > most)
                {
                    most = frames.Count;
                    best = variant;
                }
            }

            return best;
        }
    }

    /// <summary>Frames decoded by the settled variant, oldest first.</summary>
    public IReadOnlyList<YsfFich> Frames =>
        SettledVariant is { } variant ? _byVariant[variant] : [];

    /// <summary>True once a variant has proved itself — the transmission is Fusion, by CRC.</summary>
    public bool Decoded => SettledVariant is not null;

    /// <summary>Feeds one recovered C4FM symbol, normalised so the outer levels sit near ±1.</summary>
    public void Feed(double raw)
    {
        // Acquire as a true mean, then ease into the EWMA — the demodulator's 200 ms carrier
        // tracking is far too slow for a 100 ms frame, and on a four-level slicer a DC error moves
        // every decision boundary at once.
        _tracked++;
        var alpha = Math.Max(TrackingAlpha, 1.0 / _tracked);
        _dc += alpha * (raw - _dc);
        var symbol = raw - _dc;

        _level += alpha * (Math.Abs(symbol) - _level);
        if (_level > 1e-9)
        {
            // Normalise on mean absolute deviation: for equiprobable C4FM that puts the outer
            // levels near ±1.5 and the inner near ±0.5, so the magnitude decision sits at 1.
            symbol /= _level;
        }

        if (_collecting)
        {
            Collect(_inverted ? -symbol : symbol);
            return;
        }

        var (bit0, bit1) = HardDibit(symbol);
        _recent = ((_recent << 2) | ((ulong)bit0 << 1) | (ulong)bit1) & SyncMask;
        if (_seen < SyncBits / 2)
        {
            _seen++;
            return;
        }

        if (Matches(_recent, FrameSync))
        {
            Start(inverted: false);
        }
        else if (Matches(_recent, FrameSync ^ InversionMask))
        {
            Start(inverted: true);
        }
    }

    /// <summary>Forgets partial state between transmissions.</summary>
    public void Reset()
    {
        _recent = 0;
        _seen = 0;
        _collecting = false;
        _collected = 0;
        _tracked = 0;
        _dc = 0;
        _level = 1;
    }

    /// <summary>What this framer recovered, in the shape the host stores for every mode.</summary>
    public DecodedHeader? ToHeader()
    {
        if (SettledVariant is not { } variant)
        {
            return null;
        }

        var frames = _byVariant[variant];
        var fields = YsfFichDecoder.Describe(frames[^1]);
        fields.Add(new Contracts.HeaderField("Frames decoded", frames.Count.ToString()));
        fields.Add(new Contracts.HeaderField("Decoder variant", variant.ToString()));

        // Fusion carries callsigns in the data channel, not the FICH — a second decode chain that
        // is only worth building on a FICH path already proven against real air. Saying so beats an
        // empty space where a callsign would go.
        fields.Add(new Contracts.HeaderField("Callsign", "not decoded — data channel not implemented"));

        return new DecodedHeader(
            DetectedMode.Ysf,
            Callsign: null,
            YsfFichDecoder.Summarize(frames[^1], frames.Count),
            fields);
    }

    /// <summary>
    /// The frame counters as they arrived, oldest first — the evidence that settles whether this
    /// project's reading of the FICH layout is right. A real transmission counts its frames up and
    /// wraps; noise does not, and neither does a misread field.
    /// </summary>
    public string FrameNumberTrace =>
        string.Join(",", Frames.Select(f => $"{f.FrameNumber}/{f.FrameTotal}"));

    private void Collect(double symbol)
    {
        var (bit0, bit1) = SoftDibit(symbol);
        _fich[_collected++] = bit0;
        _fich[_collected++] = bit1;
        if (_collected < _fich.Length)
        {
            return;
        }

        _collecting = false;
        if (_captured.Count < MaxCapturedBlocks)
        {
            _captured.Add(_fich.ToArray());
        }

        // Once a variant has proved itself, stop searching and use it. The search is a calibration
        // expense — 192 Viterbi passes per frame, measured at 10% of a core on a live carrier —
        // and it buys nothing after the answer is known: the conventions do not change mid-over.
        // Narrowing drops the steady-state cost to a single pass, and the settled variant is logged
        // so it can eventually be pinned in code and the search retired altogether.
        var candidates = SettledVariant is { } settled
            ? [settled]
            : YsfFichDecoder.Variants;

        foreach (var variant in candidates)
        {
            if (!YsfFichDecoder.TryDecode(_fich, variant, out var fich) || fich is null)
            {
                continue;
            }

            FichCount++;
            if (!_byVariant.TryGetValue(variant, out var frames))
            {
                frames = [];
                _byVariant[variant] = frames;
            }

            frames.Add(fich);
        }
    }

    private void Start(bool inverted)
    {
        SyncCount++;
        _collecting = true;
        _inverted = inverted;
        _collected = 0;
    }

    /// <summary>
    /// Hard dibit under the C4FM mapping shared by every four-level mode here: 01 → +3, 00 → +1,
    /// 10 → −1, 11 → −3. So the first bit is the sign and the second is the magnitude.
    /// </summary>
    private static (int Bit0, int Bit1) HardDibit(double symbol) =>
        (symbol < 0 ? 1 : 0, Math.Abs(symbol) > 1 ? 1 : 0);

    /// <summary>
    /// The same decision kept soft, in [0,1], so the Viterbi decoder can weigh a marginal symbol
    /// instead of having it rounded off before it ever sees it.
    /// </summary>
    private static (double Bit0, double Bit1) SoftDibit(double symbol) =>
        (Math.Clamp(0.5 - (symbol / 2), 0, 1),
         Math.Clamp(0.5 + (Math.Abs(symbol) - 1), 0, 1));

    private static bool Matches(ulong value, ulong pattern) =>
        System.Numerics.BitOperations.PopCount(value ^ pattern) <= MaxSyncErrors;
}
