using System.Text;

namespace SignalScribe.Capture.Digital.Ysf;

/// <summary>
/// One combination of the Fusion FICH parameters that public sources leave ambiguous.
///
/// The decode chain itself is documented and unambiguous — deinterleave a 5×20 block, Viterbi
/// decode a rate-1/2 K=5 code, take the systematic half of four Golay(24,12) blocks, check a
/// CRC-16 over the result. What is *not* pinned down anywhere reachable is the handful of
/// conventions inside those steps: which way the interleaver was read, which generator polynomial
/// is transmitted first, which half of a Golay block is systematic, and how the CRC is seeded and
/// laid out. Each has two or four plausible answers, and every one of them is self-consistent when
/// wrong — a decoder built on the wrong choice simply never decodes, which is indistinguishable
/// from no traffic being present.
///
/// So the conventions are searched rather than assumed, exactly as the D-STAR framer searches
/// discriminator polarity, and for the same reason: a CRC decides. A wrong combination cannot
/// manufacture a frame, it can only stay silent, and the cost of carrying all of them is a few
/// hundred microseconds per 100 ms frame.
/// </summary>
/// <param name="ColumnMajor">Which way the 5×20 interleaver block is read back.</param>
/// <param name="G1">Generator polynomial for the first of each output dibit's bits.</param>
/// <param name="G2">Generator polynomial for the second.</param>
/// <param name="SystematicHigh">Whether a Golay(24,12) block carries its 12 data bits first or last.</param>
/// <param name="CrcSeed">CRC-16 register seed.</param>
/// <param name="CrcBigEndian">Whether the transmitted CRC is most-significant byte first.</param>
/// <param name="CorrectGolay">Whether to run Golay correction or just take the systematic bits.</param>
public readonly record struct YsfFichVariant(
    bool ColumnMajor,
    uint G1,
    uint G2,
    bool SystematicHigh,
    ushort CrcSeed,
    bool CrcBigEndian,
    bool CorrectGolay)
{
    public override string ToString() =>
        $"{(ColumnMajor ? "col" : "row")}/{G1:X2}:{G2:X2}/{(SystematicHigh ? "hi" : "lo")}/"
        + $"{CrcSeed:X4}{(CrcBigEndian ? "BE" : "LE")}/{(CorrectGolay ? "golay" : "raw")}";
}

/// <summary>
/// The Frame Information Channel that opens every Fusion frame: four CRC-protected bytes saying what
/// the frame carries and where it sits in the transmission.
///
/// The four bytes are the record and are always reported as they arrived. The named fields below are
/// this project's reading of them, and are held to a standard the raw bytes are not: a reading is
/// only trustworthy once real traffic has confirmed it, which for a frame counter means watching it
/// actually count. Until <see cref="LayoutConfirmed"/> the reading is withheld rather than shown —
/// a confidently wrong call type is worse than four honest hex bytes, and the CRC cannot help here
/// because it covers the bits whatever they are taken to mean.
/// </summary>
public sealed record YsfFich(byte[] Raw, YsfFichVariant Variant)
{
    /// <summary>FICH payload length in bytes, excluding its CRC.</summary>
    public const int Bytes = 4;

    /// <summary>
    /// Whether the field layout below has been confirmed against real air.
    ///
    /// Set this only once <see cref="FrameNumber"/> has been observed counting up to
    /// <see cref="FrameTotal"/> and resetting across consecutive frames of one real transmission —
    /// that is the one field whose correctness a live signal can prove on its own, and if it is
    /// right the layout it is read from is right. Until then the operator sees the raw bytes.
    /// </summary>
    public const bool LayoutConfirmed = false;

    private uint Word => ((uint)Raw[0] << 24) | ((uint)Raw[1] << 16) | ((uint)Raw[2] << 8) | Raw[3];

    private int Bits(int offset, int length) => (int)((Word >> (32 - offset - length)) & ((1u << length) - 1));

    /// <summary>Frame type: header, communications or terminator.</summary>
    public int FrameInformation => Bits(0, 2);

    /// <summary>Call mode — group call, individual, and so on.</summary>
    public int CallMode => Bits(4, 2);

    /// <summary>Where this frame sits in the transmission; the field a live signal can prove.</summary>
    public int FrameNumber => Bits(10, 3);

    /// <summary>How many frames the transmission runs to.</summary>
    public int FrameTotal => Bits(13, 3);

    /// <summary>What the payload carries — V/D mode, data full rate, voice full rate.</summary>
    public int DataType => Bits(20, 2);

    public string RawHex => string.Join(' ', Raw.Select(b => b.ToString("X2")));
}

/// <summary>Decodes the FICH from the 100 dibits that follow a Fusion frame sync.</summary>
public static class YsfFichDecoder
{
    /// <summary>Dibits the FICH occupies on air.</summary>
    public const int Dibits = 100;

    /// <summary>Interleaver block shape — 5 × 20 covers the 100 dibits exactly.</summary>
    private const int Rows = 5;

    private const int Columns = 20;

    /// <summary>Bits out of the Viterbi decoder: 96 of payload plus four flushing the register.</summary>
    private const int DecodedBits = 100;

    private const int PayloadBits = 96;

    /// <summary>
    /// Every parameter combination worth trying, cheapest ambiguity first. The polynomial pairs are
    /// the standard rate-1/2 K=5 generators and their bit reversals, which is what covers a register
    /// clocked the other way round.
    /// </summary>
    /// <summary>
    /// Every rate-1/2 constraint-length-5 generator pair in common use, each in both output orders.
    ///
    /// (23,35)₈ is the optimum K=5 code and the one most likely to be here; (31,27)₈ is its bit
    /// reversal, which is what a register clocked the other way round looks like; (25,37)₈ is the
    /// other pair that turns up in published tables and is its own reversal. Listing all three
    /// costs one more Viterbi pass per frame and removes a whole class of "the decoder is silent
    /// because we guessed the polynomial" from the search.
    ///
    /// Declared before <see cref="Variants"/> deliberately: static initialisers run in declaration
    /// order, and this one being second left the variant table building from a null array.
    /// </summary>
    private static readonly (uint G1, uint G2)[] PolynomialPairs =
    [
        (0x13u, 0x1Du), (0x1Du, 0x13u),   // (23,35)₈ — the optimum K=5 pair
        (0x19u, 0x17u), (0x17u, 0x19u),   // (31,27)₈ — its bit reversal
        (0x15u, 0x1Fu), (0x1Fu, 0x15u),   // (25,37)₈ — self-reversing
    ];

    public static readonly YsfFichVariant[] Variants =
    [
        .. from columnMajor in new[] { true, false }
           from poly in PolynomialPairs
           from systematicHigh in new[] { true, false }
           from seed in new ushort[] { 0xFFFF, 0x0000 }
           from bigEndian in new[] { true, false }
           from correctGolay in new[] { true, false }
           select new YsfFichVariant(columnMajor, poly.G1, poly.G2, systematicHigh, seed, bigEndian, correctGolay),
    ];

    /// <summary>
    /// Attempts one variant against 100 dibits of soft bits. Returns false unless the CRC agrees,
    /// which is the only thing separating a real FICH from noise that happened to survive Viterbi.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<double> softBits, YsfFichVariant variant, out YsfFich? fich)
    {
        fich = null;
        if (softBits.Length != Dibits * 2)
        {
            return false;
        }

        Span<double> deinterleaved = stackalloc double[Dibits * 2];
        for (var i = 0; i < Dibits; i++)
        {
            var source = variant.ColumnMajor
                ? ((i % Rows) * Columns) + (i / Rows)
                : ((i % Columns) * Rows) + (i / Columns);
            deinterleaved[2 * i] = softBits[2 * source];
            deinterleaved[(2 * i) + 1] = softBits[(2 * source) + 1];
        }

        Span<byte> decoded = stackalloc byte[DecodedBits];
        YsfConvolution.Decode(deinterleaved, decoded, variant.G1, variant.G2);

        // Four Golay(24,12) blocks, systematic half taken and the parity discarded. Correction is
        // deliberately not attempted: the Viterbi stage has already cleaned the bits up, the CRC
        // still has the final say, and the generator matrix is one more convention that would have
        // to be searched. Adding correction later only improves marginal copy.
        Span<byte> payload = stackalloc byte[48];
        for (var block = 0; block < 4; block++)
        {
            if (variant.CorrectGolay)
            {
                // Fold the block into a codeword with the data half first, whichever half it arrived
                // in, so the correction sees the layout it expects.
                uint word = 0;
                for (var bit = 0; bit < 24; bit++)
                {
                    var source = variant.SystematicHigh ? bit : (bit + 12) % 24;
                    word = (word << 1) | decoded[(block * 24) + source];
                }

                if (Golay24.Decode(word) is not { } corrected)
                {
                    return false; // too damaged to trust — let the frame fail rather than guess
                }

                for (var bit = 0; bit < 12; bit++)
                {
                    payload[(block * 12) + bit] = (byte)((corrected >> (11 - bit)) & 1);
                }

                continue;
            }

            var start = (block * 24) + (variant.SystematicHigh ? 0 : 12);
            for (var bit = 0; bit < 12; bit++)
            {
                payload[(block * 12) + bit] = decoded[start + bit];
            }
        }

        if (payload.Length > PayloadBits)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[6];
        for (var i = 0; i < 48; i++)
        {
            if (payload[i] != 0)
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        // An all-zero block is not a frame. A zero-seeded CRC over zero data is zero, so a dead
        // field — a fade driving every soft bit to the same value, or a variant reading the half of
        // a Golay block that carries no data — satisfies the CRC without any signal behind it. This
        // is the one payload the CRC cannot vouch for, so it is rejected outright.
        var empty = true;
        foreach (var b in bytes)
        {
            if (b != 0)
            {
                empty = false;
                break;
            }
        }

        if (empty)
        {
            return false;
        }

        var expected = variant.CrcBigEndian
            ? (ushort)((bytes[4] << 8) | bytes[5])
            : (ushort)((bytes[5] << 8) | bytes[4]);

        if (Crc16(bytes[..YsfFich.Bytes], variant.CrcSeed) != expected)
        {
            return false;
        }

        fich = new YsfFich(bytes[..YsfFich.Bytes].ToArray(), variant);
        return true;
    }

    /// <summary>CRC-16/CCITT, polynomial 0x1021, most-significant bit first.</summary>
    private static ushort Crc16(ReadOnlySpan<byte> data, ushort seed)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc;
    }

    /// <summary>Builds the on-air dibits for a FICH — the encoder side, so tests can state the chain both ways.</summary>
    public static void Encode(ReadOnlySpan<byte> fichBytes, YsfFichVariant variant, Span<double> softBits)
    {
        Span<byte> bytes = stackalloc byte[6];
        fichBytes[..YsfFich.Bytes].CopyTo(bytes);
        var crc = Crc16(bytes[..YsfFich.Bytes], variant.CrcSeed);
        bytes[4] = variant.CrcBigEndian ? (byte)(crc >> 8) : (byte)(crc & 0xFF);
        bytes[5] = variant.CrcBigEndian ? (byte)(crc & 0xFF) : (byte)(crc >> 8);

        Span<byte> decoded = stackalloc byte[DecodedBits];
        for (var block = 0; block < 4; block++)
        {
            // Real Golay parity, the way a transmitter sends it — so a decoder that corrects and one
            // that merely takes the systematic half both read the same frame back, and neither is
            // being flattered by a fixture that left the parity half blank.
            ushort data = 0;
            for (var bit = 0; bit < 12; bit++)
            {
                var index = (block * 12) + bit;
                data = (ushort)((data << 1) | ((bytes[index / 8] >> (7 - (index % 8))) & 1));
            }

            var codeword = Golay24.Encode(data);
            for (var bit = 0; bit < 24; bit++)
            {
                var destination = variant.SystematicHigh ? bit : (bit + 12) % 24;
                decoded[(block * 24) + destination] = (byte)((codeword >> (23 - bit)) & 1);
            }
        }

        Span<double> encoded = stackalloc double[Dibits * 2];
        YsfConvolution.Encode(decoded, encoded, variant.G1, variant.G2);

        for (var i = 0; i < Dibits; i++)
        {
            var destination = variant.ColumnMajor
                ? ((i % Rows) * Columns) + (i / Rows)
                : ((i % Columns) * Rows) + (i / Columns);
            softBits[2 * destination] = encoded[2 * i];
            softBits[(2 * destination) + 1] = encoded[(2 * i) + 1];
        }
    }

    /// <summary>The reading of a FICH, as fields — raw always, interpretation only once confirmed on air.</summary>
    public static List<Contracts.HeaderField> Describe(YsfFich fich)
    {
        var fields = new List<Contracts.HeaderField>(6)
        {
            new("FICH", fich.RawHex),
        };

#pragma warning disable CS0162 // unreachable until the layout is confirmed against real traffic
        if (YsfFich.LayoutConfirmed)
        {
            fields.Add(new Contracts.HeaderField("Frame type", fich.FrameInformation.ToString()));
            fields.Add(new Contracts.HeaderField("Call mode", fich.CallMode.ToString()));
            fields.Add(new Contracts.HeaderField("Data type", fich.DataType.ToString()));
            fields.Add(new Contracts.HeaderField("Frame", $"{fich.FrameNumber} of {fich.FrameTotal}"));
        }
#pragma warning restore CS0162

        return fields;
    }

    /// <summary>A one-line reading for lists and search.</summary>
    public static string Summarize(YsfFich fich, int frames) =>
        $"Fusion — {frames} FICH frame{(frames == 1 ? string.Empty : "s")} decoded, last {fich.RawHex}";
}
