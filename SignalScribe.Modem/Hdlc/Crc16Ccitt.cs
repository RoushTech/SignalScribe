namespace SignalScribe.Modem.Hdlc;

/// <summary>
/// CRC-16-CCITT (X.25 / HDLC FCS variant): reflected polynomial 0x8408,
/// initial value 0xFFFF, final complement.  The FCS is transmitted
/// least-significant byte first after the frame contents.
/// </summary>
public static class Crc16Ccitt
{
    private const ushort ReflectedPoly = 0x8408;

    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (ushort)i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ ReflectedPoly) : (ushort)(crc >> 1);
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Computes the FCS for <paramref name="data"/> (frame contents without the FCS).
    /// </summary>
    public static ushort ComputeFcs(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
            crc = (ushort)((crc >> 8) ^ Table[(crc ^ b) & 0xFF]);
        return (ushort)~crc;
    }

    /// <summary>
    /// Validates a frame whose final two bytes are the FCS (LSB first).
    /// </summary>
    public static bool IsFrameValid(ReadOnlySpan<byte> frameWithFcs)
    {
        if (frameWithFcs.Length < 3)
            return false;

        var expected = ComputeFcs(frameWithFcs[..^2]);
        var actual = (ushort)(frameWithFcs[^2] | (frameWithFcs[^1] << 8));
        return expected == actual;
    }
}
