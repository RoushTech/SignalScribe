using SignalScribe.Enums;

namespace SignalScribe.Capture.Digital.C4fm;

/// <summary>
/// Names the four-level modes by their sync words, from the same 4800-baud symbol stream that
/// serves the D-STAR framer. DMR, P25 Phase 1 and YSF all run 4800 symbols per second, so one
/// synchroniser feeds one detector watching all three tables.
///
/// The level histogram deliberately refuses to name any four-level mode — DMR's ±1944/±648 Hz plan
/// and the ±1800/±600 shared by P25, YSF and NXDN96 are 8% apart, within ordinary filter
/// compression and transmitter error — so per the architecture the sync pattern is what settles it,
/// and this is that detector. Every word here is a published protocol constant (ETSI TS 102 361-1
/// table 9.2 for DMR; TIA-102.BAAA for P25's frame sync; the System Fusion specification for YSF's;
/// NXDN TS 1-E §4.4.4 for NXDN's), written from the specifications, not ported from any decoder.
///
/// <para><b>There is no CRC behind a sync hit, so one hit is not a verdict.</b> Each word carries
/// its own repeat requirement, scaled to how often it would match by accident: real traffic repeats
/// its sync dozens of times a second, while random matches never arrive wearing the same name at
/// the required rate.</para>
///
/// <para><b>Polarity is searched, not assumed</b>, exactly as in the D-STAR framer: which way a
/// symbol comes out of the discriminator depends on the receiver's mixing. Inverting the signal
/// flips every symbol's sign and leaves its magnitude alone, which in the dibit packing flips
/// exactly the high bit of every dibit — one XOR mask covers it.</para>
/// </summary>
public sealed class C4fmSyncDetector
{
    private readonly record struct SyncWord(ulong Word, int Bits, DetectedMode Mode, int MaxErrors, int MinSyncs);

    /// <summary>
    /// Dibits are packed (sign, magnitude), specification-order, first symbol in the top dibit,
    /// under the shared C4FM mapping 01 → +3, 00 → +1, 10 → −1, 11 → −3.
    ///
    /// Tolerance and the repeat requirement both scale with length, because the false-match rate
    /// does: ≤3-in-48 is a ~6×10⁻¹¹ accident per position, but NXDN's word is only 20 bits, where
    /// even one bit of slack would randomly match every few seconds — so it gets zero tolerance and
    /// must repeat five times, which real NXDN does every 80 ms frame and noise never will.
    /// </summary>
    private static readonly SyncWord[] SyncWords =
    [
        // DMR, ETSI TS 102 361-1 table 9.2.
        new(0x755FD7DF75F7, 48, DetectedMode.Dmr, 3, 2), // BS sourced voice
        new(0xDFF57D75DF5D, 48, DetectedMode.Dmr, 3, 2), // BS sourced data
        new(0x7F7D5DD57DFD, 48, DetectedMode.Dmr, 3, 2), // MS sourced voice
        new(0xD5D7F77FD757, 48, DetectedMode.Dmr, 3, 2), // MS sourced data
        new(0x77D55F7DFD77, 48, DetectedMode.Dmr, 3, 2), // MS sourced reverse channel
        new(0x5D577F7757FF, 48, DetectedMode.Dmr, 3, 2), // TDMA direct mode, slot 1 voice
        new(0xF7FDD5DDFD55, 48, DetectedMode.Dmr, 3, 2), // TDMA direct mode, slot 1 data
        new(0x7DFFD5F55D5F, 48, DetectedMode.Dmr, 3, 2), // TDMA direct mode, slot 2 voice
        new(0xD7557F5FF7F5, 48, DetectedMode.Dmr, 3, 2), // TDMA direct mode, slot 2 data

        // P25 Phase 1 frame sync, TIA-102.BAAA — every P25 data unit opens with it.
        new(0x5575F5FF77FF, 48, DetectedMode.P25Phase1, 3, 2),

        // YSF frame sync, from the System Fusion specification — heads every 40 ms frame.
        new(0xD471C9634D, 40, DetectedMode.Ysf, 2, 2),

        // NXDN frame sync word, NXDN TS 1-E §4.4.4: symbols −3,+1,−3,+3,−3,−3,+3,+3,−1,+3. This
        // names NXDN96 only — NXDN48 runs 2400 symbols per second, which the shared 4800-baud
        // synchroniser does not recover.
        new(0xCDF59, 20, DetectedMode.Nxdn, 0, 5),
    ];

    private const int MaxSyncSymbols = 24;

    /// <summary>
    /// Time constant for the detector's own DC and level tracking, in symbols. The same reasoning as
    /// the D-STAR framer: the demodulator's 200 ms carrier EWMA is still crawling when the sync has
    /// been and gone, and a residual DC bias lands straight on the sign decision. Acquire as a true
    /// mean, ease into the EWMA.
    /// </summary>
    private const double TrackingAlpha = 1.0 / 64;

    private double _dc;

    private double _level = 1;

    private long _tracked;

    private ulong _recent;

    private int _fresh;

    private readonly int[] _hits = new int[Enum.GetValues<DetectedMode>().Length];

    /// <summary>Sync hits seen for one mode — surfaced for diagnostics and threshold arguments.</summary>
    public int SyncCount(DetectedMode mode) => _hits[(int)mode];

    /// <summary>
    /// The mode whose sync repeated enough to be named, or <see cref="DetectedMode.Unknown"/>. Ties
    /// cannot realistically arise — two modes' syncs matching in one transmission is already deep
    /// into accident territory — so the highest count wins without ceremony.
    /// </summary>
    public DetectedMode Named
    {
        get
        {
            var best = DetectedMode.Unknown;
            foreach (var candidate in SyncWords)
            {
                var hits = _hits[(int)candidate.Mode];
                if (hits >= candidate.MinSyncs && (best == DetectedMode.Unknown || hits > _hits[(int)best]))
                {
                    best = candidate.Mode;
                }
            }

            return best;
        }
    }

    /// <summary>Feeds one recovered symbol, in the demodulator's deviation units (Hz).</summary>
    public void Feed(double raw)
    {
        _tracked++;
        var alpha = Math.Max(TrackingAlpha, 1.0 / _tracked);

        _dc += alpha * (raw - _dc);
        var symbol = raw - _dc;

        // Mean |symbol| of balanced four-level traffic sits between the inner and outer levels, so
        // it is the magnitude threshold: above it reads outer (±3), below it inner (±1).
        _level += alpha * (Math.Abs(symbol) - _level);

        var sign = symbol < 0 ? 1UL : 0UL;
        var outer = Math.Abs(symbol) > _level ? 1UL : 0UL;
        _recent = (_recent << 2) | (sign << 1) | outer;

        if (_fresh < MaxSyncSymbols)
        {
            // Not enough new symbols since the last hit (or the start) for a distinct word.
            _fresh++;
            return;
        }

        foreach (var candidate in SyncWords)
        {
            var mask = candidate.Bits == 64 ? ulong.MaxValue : (1UL << candidate.Bits) - 1;
            var window = _recent & mask;

            // Polarity: an inverted discriminator flips every dibit's sign bit and nothing else.
            var polarity = 0xAAAAAAAAAAAAAAAA & mask;
            var direct = ulong.PopCount(window ^ candidate.Word);
            var inverted = ulong.PopCount(window ^ candidate.Word ^ polarity);
            if (Math.Min(direct, inverted) <= (uint)candidate.MaxErrors)
            {
                _hits[(int)candidate.Mode]++;
                _fresh = 0; // a hit may not count again until a full new word has shifted in
                return;
            }
        }
    }
}
