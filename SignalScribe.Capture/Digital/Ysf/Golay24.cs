namespace SignalScribe.Capture.Digital.Ysf;

/// <summary>
/// The extended binary Golay(24,12) code, which protects Fusion's FICH beneath its convolutional
/// layer — twelve data bits carried in twenty-four, correcting any three bit errors.
///
/// Skipping the correction and simply taking the systematic half is tempting and is what the openly
/// available GNU Radio decoder does. It is also the wrong call on a marginal signal: measured on air,
/// 145.310 produced frame syncs on only three or four of the ten-odd frames in each transmission,
/// which is a symbol error rate high enough that a hundred-dibit FICH will carry errors nearly every
/// time. The Viterbi stage cleans up some of it; this cleans up the rest, and it is the layer the
/// protocol put there for exactly this reason.
///
/// <para>Written from the code's algebra — the generator is a published mathematical object and the
/// syndrome decoder is the textbook algorithm — rather than ported from any GPLv2 decoder.</para>
/// </summary>
public static class Golay24
{
    /// <summary>
    /// The standard B matrix: the generator is [I | B], and B is its own inverse in the sense the
    /// decoder relies on (B = Bᵀ up to the row ordering used here).
    /// </summary>
    private static readonly ushort[] B =
    [
        0xDC5, 0xB8B, 0x717, 0xE2D, 0xC5B, 0x8B7,
        0x16F, 0x2DD, 0x5B9, 0xB71, 0x6E3, 0xFFE,
    ];

    /// <summary>Encodes 12 data bits into a 24-bit codeword, data in the high half.</summary>
    public static uint Encode(ushort data)
    {
        var parity = Parity(data);
        return ((uint)(data & 0xFFF) << 12) | parity;
    }

    /// <summary>
    /// Corrects up to three bit errors and returns the 12 data bits, or null when the word is too
    /// damaged to be trusted. Returning null matters: a fourth error decodes to the wrong codeword
    /// with total confidence, so the CRC above must be given a chance to reject it rather than being
    /// handed a plausible-looking guess.
    /// </summary>
    public static ushort? Decode(uint codeword)
    {
        var data = (ushort)((codeword >> 12) & 0xFFF);
        var received = (ushort)(codeword & 0xFFF);

        // Errors confined to the parity half.
        var syndrome = (ushort)(Parity(data) ^ received);
        if (Weight(syndrome) <= 3)
        {
            return data;
        }

        // A single data-bit error plus up to two parity errors.
        for (var i = 0; i < 12; i++)
        {
            if (Weight((ushort)(syndrome ^ B[i])) <= 2)
            {
                return (ushort)(data ^ (1 << (11 - i)));
            }
        }

        // Errors confined to the data half, found by carrying the syndrome back through B.
        var carried = Parity(syndrome);
        if (Weight(carried) <= 3)
        {
            return (ushort)(data ^ carried);
        }

        // A single parity-bit error alongside up to two data errors. Only the data half is returned,
        // so the parity bit's own correction is not applied here — folding it in was a bug: it put a
        // parity-side error into the data word and broke every three-error correction.
        for (var i = 0; i < 12; i++)
        {
            if (Weight((ushort)(carried ^ B[i])) <= 2)
            {
                return (ushort)(data ^ carried ^ B[i]);
            }
        }

        return null;
    }

    /// <summary>Multiplies a 12-bit vector by B — the parity half of its codeword.</summary>
    private static ushort Parity(ushort data)
    {
        ushort result = 0;
        for (var i = 0; i < 12; i++)
        {
            if ((data & (1 << (11 - i))) != 0)
            {
                result ^= B[i];
            }
        }

        return result;
    }

    private static int Weight(ushort value) => System.Numerics.BitOperations.PopCount(value);
}
