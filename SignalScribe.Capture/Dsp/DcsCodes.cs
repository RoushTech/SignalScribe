namespace SignalScribe.Capture.Dsp;

/// <summary>
/// Digital Coded Squelch. A continuous 134.4 bps sub-audible stream repeating a 23-bit word:
/// 9 data bits (the octal code), 11 Golay parity bits, and 3 fixed bits (100). Unlike CTCSS there
/// is no tone to measure — you decode it.
/// </summary>
public static class DcsCodes
{
    /// <summary>The standard code set, as the octal numbers operators actually quote (023, 754, ...).</summary>
    public static readonly int[] Standard =
    [
        023, 025, 026, 031, 032, 043, 047, 051, 054, 065, 071, 072, 073, 074,
        114, 115, 116, 125, 131, 132, 134, 143, 152, 155, 156, 162, 165, 172, 174,
        205, 223, 226, 243, 244, 245, 251, 261, 263, 265, 271,
        306, 311, 315, 331, 343, 346, 351, 364, 365, 371,
        411, 412, 413, 423, 431, 432, 445, 464, 465, 466,
        503, 506, 516, 532, 546, 565,
        606, 612, 624, 627, 631, 632, 654, 662, 664,
        703, 712, 723, 731, 732, 734, 743, 754,
    ];

    /// <summary>
    /// Builds the 23-bit word a radio transmits for an octal code: 3 fixed bits, then the 9 data
    /// bits, then 11 Golay(23,12) parity bits, sent least-significant first.
    /// </summary>
    public static int Encode(int octalCode)
    {
        var data = 0;
        var digits = octalCode;
        for (var shift = 0; digits > 0; shift += 3)
        {
            data |= (digits % 10) << shift;
            digits /= 10;
        }

        // 12 information bits = 9 code bits + the fixed "100" tail, then Golay parity over them.
        var info = (data & 0x1FF) | (0b100 << 9);
        return info | (Golay(info) << 12);
    }

    /// <summary>
    /// Golay(23,12) parity for 12 information bits: the remainder of info(x)*x^11 modulo the
    /// generator 0xC75. Minimum distance 7, so a single flipped bit can never masquerade as a
    /// different valid codeword.
    /// </summary>
    public static int Golay(int info)
    {
        var reg = (info & 0xFFF) << 11;
        for (var bit = 22; bit >= 11; bit--)
        {
            if (((reg >> bit) & 1) != 0)
            {
                reg ^= 0xC75 << (bit - 11);
            }
        }

        return reg & 0x7FF;
    }

    /// <summary>Recovers the octal code from a 23-bit word, or null if its parity does not check out.</summary>
    public static int? Decode(int word23)
    {
        var info = word23 & 0xFFF;
        if (((word23 >> 12) & 0x7FF) != Golay(info) || ((info >> 9) & 0b111) != 0b100)
        {
            return null;
        }

        var data = info & 0x1FF;
        return ((data >> 6) & 7) * 100 + ((data >> 3) & 7) * 10 + (data & 7);
    }
}
