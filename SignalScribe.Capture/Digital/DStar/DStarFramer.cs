namespace SignalScribe.Capture.Digital.DStar;

/// <summary>
/// Finds D-STAR headers in a stream of recovered symbols.
///
/// A transmission opens with a long run of alternating bits for the clock to catch, then a 24-bit
/// frame sync, then the 660 coded bits of the header. This watches for the sync, collects what
/// follows, and hands it to <see cref="DStarHeaderFec"/>.
///
/// <para><b>Polarity is searched, not assumed.</b> Whether a one comes out of the discriminator
/// positive or negative depends on which way the receiver mixes, and nothing upstream establishes it
/// — so the sync is matched against both the pattern and its inverse, and whichever hits fixes the
/// polarity for the header that follows. Real D-STAR radios do the same.</para>
///
/// <para><b>The CRC is the real gate.</b> That lets sync detection be generous — a couple of wrong
/// bits are tolerated, and both polarities are tried — because the worst a false sync can do is cost
/// one Viterbi decode that then fails its CRC. Being strict here would lose marginal headers; being
/// generous cannot manufacture one.</para>
/// </summary>
public sealed class DStarFramer
{
    /// <summary>
    /// The 24-bit header frame sync.
    ///
    /// Worth knowing: this constant and the interleave orientation in <see cref="DStarHeaderFec"/>
    /// are the two things in the D-STAR path that a synthetic round-trip cannot confirm, because both
    /// are self-consistent when wrong. Published sources disagree on how it is written down — the
    /// same pattern appears variously reversed or byte-swapped — which is exactly why both polarities
    /// are searched and why the CRC is what decides.
    /// </summary>
    public const uint HeaderFrameSync = 0xEAA060;

    private const int SyncBits = 24;

    private const uint SyncMask = (1u << SyncBits) - 1;

    /// <summary>
    /// Sync bits allowed to be wrong. The pattern is 24 bits, so two errors is still a ~1-in-10⁴
    /// accident before the CRC even looks at it, and a header arriving at the edge of copy is worth
    /// more than the false starts this admits.
    /// </summary>
    private const int MaxSyncErrors = 2;

    /// <summary>
    /// The voice-frame sync: every 21st frame's 24-bit data segment carries, per the JARL
    /// specification, a 10-bit alternating pattern followed by two 7-bit "1101000" M-sequences —
    /// chronologically 101010101011010001101000, packed here oldest-bit-highest like
    /// <see cref="HeaderFrameSync"/>. (The same sequence is often quoted as bytes 55 2D 16, LSB
    /// first; both derivations agree, which is the cross-check the header sync never had.)
    ///
    /// Its alternating half is indistinguishable from the bit-sync preamble, so the discrimination
    /// lives entirely in the 14 M-sequence bits — which differ from pure alternation in 8 places,
    /// comfortably clear of the 2-bit tolerance.
    /// </summary>
    public const uint VoiceFrameSync = 0xAAB468;

    /// <summary>
    /// Cadenced frame syncs required before a transmission may be *named* D-STAR without a decoded
    /// header. There is no CRC behind this pattern, so repetition stands in for proof — but only
    /// repetition *on schedule*. A raw count is nowhere near enough: the sync is 24 bits matched
    /// with two errors' tolerance in both polarities, which is 602 accepted windows out of 2²⁴ —
    /// one accident every ~6 s of noise-shaped input at 4800 baud, and the squelch tail, repeater
    /// hang and pre-roll all feed this framer exactly that. Measured against that rate, any long FM
    /// clip reaches three raw hits, which is how an analog ragchew got itself labelled D-STAR and
    /// silently excluded from transcription. The C4FM detector never had this problem because its
    /// words are 48 bits at ≤3 errors (~10⁻¹⁰ per symbol); a 24-bit word must earn trust the way
    /// real D-STAR actually behaves — arriving every 420 ms, in one polarity, for as long as the
    /// carrier stands (see <see cref="SyncPeriodSymbols"/>).
    /// </summary>
    public const int MinFrameSyncs = 3;

    /// <summary>
    /// Symbols from one voice-frame sync start to the next: the sync rides the 24-bit data segment
    /// of every 21st frame, and a DV frame is 96 bits — 21 × 96 = 2016 symbols, 420 ms at 4800
    /// baud. Two accidental matches landing a multiple of this apart (±a few symbols) is a
    /// ~2×10⁻³ coincidence per hit; three in a row is what real D-STAR does and noise does not.
    /// </summary>
    public const int SyncPeriodSymbols = 21 * 96;

    /// <summary>
    /// Sync periods a chain may skip and still continue: a fade can eat a sync or two without the
    /// transmission having ended, and a missed sync must not send the count back to zero.
    /// </summary>
    private const int MaxMissedPeriods = 4;

    /// <summary>
    /// Time constant for the framer's own DC and level tracking, in symbols.
    ///
    /// It cannot lean on the demodulator's carrier tracking, which is an EWMA with a 200 ms time
    /// constant — tuned for holding a voice conversation steady, and still crawling toward the true
    /// carrier when a 190 ms D-STAR header has already been and gone. The residual offset lands
    /// straight on the slicer, and for a two-level decision a DC bias *is* the error: measured, a
    /// carrier 2.5 kHz off its bin decoded nothing at all until this was added.
    ///
    /// Sixty-four symbols is short enough to settle inside the bit-sync preamble and long enough that
    /// it tracks the carrier rather than the data — which is safe here because both the preamble and
    /// the scrambled payload are balanced over that span.
    /// </summary>
    private const double TrackingAlpha = 1.0 / 64;

    private double _dc;

    private double _level = 1;

    private long _tracked;

    private uint _recent;

    private int _seen;

    private bool _collecting;

    private bool _inverted;

    private readonly double[] _payload = new double[DStarHeaderFec.ChannelBits];

    private int _collected;

    /// <summary>A voice-frame sync hit: where it landed, which polarity matched, and the longest on-cadence run it ends.</summary>
    private readonly record struct VoiceSyncHit(long Symbol, bool Inverted, int Run);

    private readonly List<VoiceSyncHit> _voiceSyncHits = [];

    private int _bestCadenceRun;

    /// <summary>Raised for each header that passes its CRC.</summary>
    public event Action<DStarHeader>? HeaderDecoded;

    /// <summary>Headers recovered so far.</summary>
    public int HeaderCount { get; private set; }

    /// <summary>Sync patterns matched, including those whose header then failed its CRC.</summary>
    public int SyncCount { get; private set; }

    /// <summary>
    /// Voice-frame syncs matched, cadenced or not. Diagnostics only — on a long analog clip this
    /// accumulates accidental hits at a steady rate, which is precisely why it is not the verdict.
    /// </summary>
    public int FrameSyncCount { get; private set; }

    /// <summary>Longest run of voice-frame syncs arriving on the 420 ms cadence in one polarity.</summary>
    public int CadencedFrameSyncs => _bestCadenceRun;

    /// <summary>
    /// True when the transmission is recognisably D-STAR even though no header survived its CRC —
    /// the marginal-signal case: a header is one 190 ms chance per transmission, while the frame
    /// sync returns every 420 ms for as long as the carrier stands. What qualifies is a *cadenced*
    /// run: <see cref="MinFrameSyncs"/> syncs spaced multiples of <see cref="SyncPeriodSymbols"/>
    /// apart, all in one polarity — a schedule accidental matches on analog traffic do not keep.
    /// </summary>
    public bool VoiceFramesSeen => _bestCadenceRun >= MinFrameSyncs;

    /// <summary>
    /// Feeds one recovered symbol. Positive is a one before polarity is resolved; magnitude is the
    /// slicer's confidence and is carried through to the Viterbi decoder.
    /// </summary>
    public void Feed(double raw)
    {
        // Acquire fast, then settle. A plain EWMA at the tracking rate is only ~78% converged by the
        // end of a bit-sync preamble, and a DC estimate that is still moving when the frame sync
        // arrives loses the sync outright. Running as a true mean for the first symbols and easing
        // into the EWMA converges in a few symbols without giving up the steady-state smoothing.
        _tracked++;
        var alpha = Math.Max(TrackingAlpha, 1.0 / _tracked);

        _dc += alpha * (raw - _dc);
        var symbol = raw - _dc;

        // Normalise as well as centre, so the soft values reaching the Viterbi decoder mean the same
        // thing regardless of how hard the station was deviating.
        _level += alpha * (Math.Abs(symbol) - _level);
        if (_level > 1e-9)
        {
            symbol /= _level;
        }

        if (_collecting)
        {
            _payload[_collected++] = _inverted ? -symbol : symbol;
            if (_collected < _payload.Length)
            {
                return;
            }

            _collecting = false;
            if (DStarHeaderFec.TryDecode(_payload, out var header))
            {
                HeaderCount++;
                HeaderDecoded?.Invoke(DStarHeader.Parse(header));
            }

            return;
        }

        _recent = ((_recent << 1) | (symbol >= 0 ? 1u : 0u)) & SyncMask;
        if (_seen < SyncBits)
        {
            _seen++;
            return;
        }

        if (Matches(_recent, HeaderFrameSync))
        {
            Start(inverted: false);
        }
        else if (Matches(_recent, ~HeaderFrameSync & SyncMask))
        {
            Start(inverted: true);
        }
        else if (Matches(_recent, VoiceFrameSync))
        {
            RecordVoiceSync(inverted: false);
        }
        else if (Matches(_recent, ~VoiceFrameSync & SyncMask))
        {
            RecordVoiceSync(inverted: true);
        }
    }

    /// <summary>
    /// Chains a voice-frame sync hit onto any earlier hit it follows on cadence. Kept as a
    /// longest-run search over recent hits rather than a single last-hit comparison, so a stray
    /// accidental match landing between two genuine syncs cannot break a real chain — the genuine
    /// ones still chain to each other straight past it.
    /// </summary>
    private void RecordVoiceSync(bool inverted)
    {
        FrameSyncCount++;
        _seen = 0; // a hit may not count again until a full new window has shifted in

        var run = 1;
        foreach (var hit in _voiceSyncHits)
        {
            // A transmission's polarity is fixed by the receiver's mixing; noise flips a coin.
            if (hit.Inverted != inverted)
            {
                continue;
            }

            var spacing = _tracked - hit.Symbol;
            var periods = (int)Math.Round(spacing / (double)SyncPeriodSymbols);
            if (periods < 1 || periods > MaxMissedPeriods + 1)
            {
                continue;
            }

            // ±(2 + periods) symbols: room for a timing-loop slip and 100 ppm of transmitter clock
            // error, growing with how long the gap is — and still narrow enough that a random hit
            // lands in a window this size about twice in a thousand tries.
            if (Math.Abs(spacing - ((long)periods * SyncPeriodSymbols)) > 2 + periods)
            {
                continue;
            }

            run = Math.Max(run, hit.Run + 1);
        }

        _voiceSyncHits.Add(new VoiceSyncHit(_tracked, inverted, run));
        _voiceSyncHits.RemoveAll(h => _tracked - h.Symbol > ((MaxMissedPeriods + 1) * (long)SyncPeriodSymbols) + 32);
        _bestCadenceRun = Math.Max(_bestCadenceRun, run);
    }

    /// <summary>Forgets partial state between transmissions, so a truncated header cannot bleed into the next one.</summary>
    public void Reset()
    {
        _recent = 0;
        _seen = 0;
        _collecting = false;
        _collected = 0;
        _tracked = 0;
        _dc = 0;
        _level = 1;
        _voiceSyncHits.Clear(); // positions are stream-relative, meaningless across the gap
    }

    private void Start(bool inverted)
    {
        SyncCount++;
        _collecting = true;
        _inverted = inverted;
        _collected = 0;
    }

    private static bool Matches(uint value, uint pattern) =>
        System.Numerics.BitOperations.PopCount(value ^ pattern) <= MaxSyncErrors;
}
