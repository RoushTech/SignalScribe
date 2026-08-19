namespace SignalScribe.Capture.Digital.DStar;

/// <summary>
/// The D-STAR DV header's error-correction chain, in reverse: 660 on-wire bits back to the 41-byte
/// routing block that names who is calling whom.
///
/// The header is sent once, at the head of a transmission, so unlike the voice frames it cannot lean
/// on repetition. The JARL specification protects it heavily instead:
/// <code>
///   41 bytes (328 bits) + 4-bit flush tail
///     → K=5 rate-1/2 convolutional code (G1 = 0x19, G2 = 0x17)   → 664 bits
///     → puncture the 4 trailing bits                             → 660 bits
///     → scramble with a 7-stage PN sequence
///     → 24-column block interleave
///     = 660 on-wire bits
/// </code>
/// Decoding runs it backwards — deinterleave, descramble (XOR is its own inverse), Viterbi — and then
/// the CRC in the last two header bytes is the gate that says whether any of it can be believed.
///
/// <para><b>Licensing.</b> None of this is ported. Every open D-STAR header decoder in circulation
/// traces back to one GPLv2-<i>only</i> implementation by G4KLX, which is incompatible with this
/// project's GPLv3 — DSDcc carries that file's original notice unchanged despite being v3 itself, and
/// DSD is the same code. The convolutional polynomials, the CRC and the frame sync are published
/// protocol facts; the scrambler was derived here from its polynomial rather than lifted as a table
/// (see <see cref="ScramblerPolynomial"/>); and the interleave is the specification's own permutation,
/// which any conforming decoder must reproduce exactly to interoperate at all.</para>
///
/// <para><b>Verification status.</b> Round-trips against the encoder below, and the scrambler matches
/// the published sequence. It has <i>not</i> yet decoded a real off-air header — that is the only
/// thing that will confirm the interleave orientation and the bit ordering within the convolutional
/// code, both of which round-trip happily while being wrong.</para>
/// </summary>
public static class DStarHeaderFec
{
    /// <summary>Header information field, in bytes: flags, four callsigns, and a CRC.</summary>
    public const int HeaderBytes = 41;

    /// <summary>On-wire bits carrying the header after coding, scrambling and interleaving.</summary>
    public const int ChannelBits = 660;

    /// <summary>Constraint length. The encoder flushes with K-1 zero bits so the survivor path ends in a known state.</summary>
    private const int ConstraintLength = 5;

    private const int TailBits = ConstraintLength - 1;

    private const int InfoBits = HeaderBytes * 8;

    private const int States = 1 << TailBits;

    /// <summary>Generator polynomials, the pair every D-STAR implementation uses: 1+x³+x⁴ and 1+x+x²+x⁴.</summary>
    private const int G1 = 0x19, G2 = 0x17;

    /// <summary>
    /// The scrambler is a maximal-length 7-stage shift register — x⁷ + x⁴ + 1, register seeded to 7,
    /// taken from the top bit. Its period is 127, which is why the precomputed tables published
    /// elsewhere visibly repeat every 127 bits; generating it is equivalent and avoids copying one.
    /// </summary>
    public const int ScramblerPolynomial = 0x48;   // taps at stages 7 and 4

    private const int ScramblerSeed = 0x07;

    /// <summary>
    /// Decodes 660 on-wire bits into the 41-byte header, or returns false if the CRC rejects it.
    ///
    /// Takes <em>soft</em> bits — a positive value means a one, and the magnitude is how confident
    /// the slicer was. The Viterbi decoder uses that confidence, which is most of the coding gain the
    /// header has: a header that is merely marginal decodes where a hard-sliced one would not.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<double> softBits, out byte[] header)
    {
        header = [];
        if (softBits.Length < ChannelBits)
        {
            return false;
        }

        Span<double> deinterleaved = stackalloc double[ChannelBits];
        Deinterleave(softBits, deinterleaved);
        Descramble(deinterleaved);

        var decoded = ViterbiDecode(deinterleaved);
        header = Pack(decoded);
        return DStarHeader.CrcMatches(header);
    }

    /// <summary>Encodes a 41-byte header into its 660 on-wire bits. Exists so the decoder can be tested against it.</summary>
    public static bool TryEncode(ReadOnlySpan<byte> header, Span<byte> channelBits)
    {
        if (header.Length != HeaderBytes || channelBits.Length < ChannelBits)
        {
            return false;
        }

        Span<byte> input = stackalloc byte[InfoBits + TailBits];
        for (var i = 0; i < InfoBits; i++)
        {
            // Bits leave the register most significant first.
            input[i] = (byte)((header[i / 8] >> (7 - (i % 8))) & 1);
        }

        // The tail is already zero — that is what drives the encoder back to state 0.
        Span<byte> coded = stackalloc byte[(InfoBits + TailBits) * 2];
        var state = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var register = (state << 1) | input[i];
            coded[2 * i] = Parity(register & G1);
            coded[(2 * i) + 1] = Parity(register & G2);
            state = register & (States - 1);
        }

        // Puncture: drop the four trailing channel bits to land on the spec's 660-bit window.
        Span<double> soft = stackalloc double[ChannelBits];
        for (var i = 0; i < ChannelBits; i++)
        {
            soft[i] = coded[i] == 1 ? 1 : -1;
        }

        Scramble(soft);

        Span<double> interleaved = stackalloc double[ChannelBits];
        Interleave(soft, interleaved);

        for (var i = 0; i < ChannelBits; i++)
        {
            channelBits[i] = (byte)(interleaved[i] > 0 ? 1 : 0);
        }

        return true;
    }

    /// <summary>
    /// The specification's block interleave, expressed as the permutation it is: step 24 positions at
    /// a time and fold back through the ragged end, which spreads consecutive coded bits far enough
    /// apart that a fade takes one bit from many codewords rather than many bits from one.
    /// </summary>
    private static void Interleave(ReadOnlySpan<double> source, Span<double> destination)
    {
        var k = 0;
        for (var i = 0; i < ChannelBits; i++)
        {
            destination[i] = source[k];
            k = NextInterleaveIndex(k);
        }
    }

    private static void Deinterleave(ReadOnlySpan<double> source, Span<double> destination)
    {
        var k = 0;
        for (var i = 0; i < ChannelBits; i++)
        {
            destination[k] = source[i];
            k = NextInterleaveIndex(k);
        }
    }

    private static int NextInterleaveIndex(int k)
    {
        k += 24;
        if (k >= 672)
        {
            return k - 671;
        }

        return k >= ChannelBits ? k - 647 : k;
    }

    private static void Descramble(Span<double> bits) => Scramble(bits);

    /// <summary>XOR against the PN sequence. On soft bits an XOR with one is a sign flip.</summary>
    private static void Scramble(Span<double> bits)
    {
        var register = ScramblerSeed;
        for (var i = 0; i < bits.Length; i++)
        {
            var bit = (register >> (ConstraintLength + 1)) & 1;   // top stage of the 7-bit register
            if (bit == 1)
            {
                bits[i] = -bits[i];
            }

            var feedback = ((register >> 6) ^ (register >> 3)) & 1;
            register = ((register << 1) | feedback) & 0x7F;
        }
    }

    /// <summary>
    /// Soft-decision Viterbi over the 16 states of a K=5 code.
    ///
    /// The four punctured channel bits are simply absent from the input; the last two input bits are
    /// therefore decoded on half the usual evidence, which matters not at all because they are the
    /// flush tail rather than header content.
    /// </summary>
    private static byte[] ViterbiDecode(ReadOnlySpan<double> soft)
    {
        var steps = InfoBits + TailBits;
        var metric = new double[States];
        var next = new double[States];
        var survivors = new byte[steps, States];

        for (var s = 1; s < States; s++)
        {
            metric[s] = double.PositiveInfinity;   // the encoder starts in state 0
        }

        for (var step = 0; step < steps; step++)
        {
            // Beyond the puncture there is no evidence for the second branch bit; a zero soft value
            // is exactly "no opinion" and costs both hypotheses equally.
            var a = (2 * step) < soft.Length ? soft[2 * step] : 0;
            var b = ((2 * step) + 1) < soft.Length ? soft[(2 * step) + 1] : 0;

            Array.Fill(next, double.PositiveInfinity);

            for (var state = 0; state < States; state++)
            {
                if (double.IsPositiveInfinity(metric[state]))
                {
                    continue;
                }

                for (var bit = 0; bit <= 1; bit++)
                {
                    var register = (state << 1) | bit;
                    var expectedA = Parity(register & G1) == 1 ? 1 : -1;
                    var expectedB = Parity(register & G2) == 1 ? 1 : -1;

                    // Correlation, negated so the search stays a minimisation.
                    var branch = -((a * expectedA) + (b * expectedB));
                    var candidate = metric[state] + branch;
                    var target = register & (States - 1);

                    if (candidate < next[target])
                    {
                        next[target] = candidate;
                        survivors[step, target] = (byte)state;
                    }
                }
            }

            Array.Copy(next, metric, States);
        }

        // The encoder flushes to state 0, so that is where the traceback starts — no search needed.
        var bits = new byte[steps];
        var current = 0;
        for (var step = steps - 1; step >= 0; step--)
        {
            var previous = survivors[step, current];
            bits[step] = (byte)(current & 1);
            current = previous;
        }

        return bits;
    }

    private static byte[] Pack(byte[] bits)
    {
        var header = new byte[HeaderBytes];
        for (var i = 0; i < InfoBits; i++)
        {
            if (bits[i] != 0)
            {
                header[i / 8] |= (byte)(1 << (7 - (i % 8)));
            }
        }

        return header;
    }

    private static byte Parity(int value)
    {
        var parity = 0;
        while (value != 0)
        {
            parity ^= value & 1;
            value >>= 1;
        }

        return (byte)parity;
    }
}
