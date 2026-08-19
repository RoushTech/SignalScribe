using System.Text;

namespace SignalScribe.Capture.Digital.DStar;

/// <summary>
/// The routing block that opens every D-STAR transmission: who is calling, who they are calling, and
/// through which repeaters.
///
/// This is the part worth having without a vocoder. The callsigns are plain ASCII in the header —
/// no AMBE, no patents, no decoding of the voice at all — so a D-STAR channel can name its stations
/// even while the audio stays opaque.
/// </summary>
/// <param name="Flags">The three flag bytes. Bit 3 of the first marks an emergency (EMR) transmission.</param>
/// <param name="RepeaterTarget">RPT2 — the repeater or gateway the traffic is bound for.</param>
/// <param name="RepeaterSource">RPT1 — the repeater it entered through.</param>
/// <param name="Urcall">UR — who is being called. "CQCQCQ" is a general call; a "/" prefix is repeater routing.</param>
/// <param name="Mycall">MY — the transmitting station.</param>
/// <param name="MycallSuffix">The four-character note a station appends to its own callsign, e.g. "MOBI".</param>
public sealed record DStarHeader(
    byte[] Flags,
    string RepeaterTarget,
    string RepeaterSource,
    string Urcall,
    string Mycall,
    string MycallSuffix)
{
    /// <summary>Header length in bytes, including the two CRC bytes.</summary>
    public const int Length = 41;

    /// <summary>A general call rather than a directed one — the usual case on a repeater.</summary>
    public bool IsGroupCall => Urcall.StartsWith("CQCQCQ", StringComparison.Ordinal);

    /// <summary>The station declared this an emergency transmission.</summary>
    public bool IsEmergency => Flags.Length > 0 && (Flags[0] & 0x08) != 0;

    /// <summary>Callsign with its suffix, the way operators write it: "KD9ABC /MOBI".</summary>
    public string MycallWithSuffix =>
        string.IsNullOrWhiteSpace(MycallSuffix) ? Mycall : $"{Mycall} /{MycallSuffix}";

    /// <summary>Parses the 41-byte header. Assumes the CRC has already been checked.</summary>
    public static DStarHeader Parse(ReadOnlySpan<byte> header) => new(
        header[..3].ToArray(),
        Callsign(header.Slice(3, 8)),
        Callsign(header.Slice(11, 8)),
        Callsign(header.Slice(19, 8)),
        Callsign(header.Slice(27, 8)),
        Callsign(header.Slice(35, 4)));

    /// <summary>
    /// Whether the CRC in the last two bytes matches the rest. This is the only thing standing
    /// between a real header and sixteen plausible-looking callsign characters recovered from noise,
    /// so nothing downstream should ever see a header that has not passed it.
    /// </summary>
    public static bool CrcMatches(ReadOnlySpan<byte> header)
    {
        if (header.Length != Length)
        {
            return false;
        }

        var expected = (ushort)(header[39] | (header[40] << 8));
        return Crc(header[..39]) == expected;
    }

    /// <summary>Writes the CRC into the last two bytes of a header being assembled.</summary>
    public static void StampCrc(Span<byte> header)
    {
        var crc = Crc(header[..39]);
        header[39] = (byte)(crc & 0xFF);
        header[40] = (byte)(crc >> 8);
    }

    /// <summary>
    /// CRC-16/CCITT as D-STAR uses it: polynomial 0x1021 reflected to 0x8408, register seeded to all
    /// ones, and the result inverted.
    /// </summary>
    private static ushort Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1;
            }
        }

        return (ushort)~crc;
    }

    /// <summary>
    /// Callsign field as text. The field is space-padded and any byte outside printable ASCII means
    /// this is not really a header, so it is rendered as '?' rather than silently dropped — a
    /// callsign that looks subtly wrong is worse than one that looks obviously wrong.
    /// </summary>
    private static string Callsign(ReadOnlySpan<byte> field)
    {
        var builder = new StringBuilder(field.Length);
        foreach (var b in field)
        {
            builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        }

        return builder.ToString().TrimEnd();
    }
}
