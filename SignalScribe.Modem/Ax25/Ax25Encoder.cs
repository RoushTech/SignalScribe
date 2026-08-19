using System.Text;

namespace SignalScribe.Modem.Ax25;

/// <summary>
/// Encodes AX.25 UI frames as raw bytes (without flags or FCS) suitable for
/// KISS transmission or HDLC framing.
/// </summary>
public static class Ax25Encoder
{
    /// <summary>APRS destination — "APRS" is the standard tocall for generic APRS.</summary>
    public const string DefaultDestination = "APRS";

    /// <summary>
    /// Builds a raw AX.25 UI frame for an APRS transmission.
    /// </summary>
    /// <param name="sourceCallsign">Source callsign, e.g. "N0CALL-10".</param>
    /// <param name="aprsInfo">APRS information field (ASCII).</param>
    /// <param name="path">
    /// Comma-separated digipeater path (e.g. "WIDE1-1,WIDE2-1").
    /// Pass an empty string for direct/no-path operation.
    /// </param>
    /// <param name="destination">Destination tocall; defaults to "APRS".</param>
    public static byte[] EncodeUiFrame(
        string sourceCallsign,
        string aprsInfo,
        string path,
        string destination = DefaultDestination)
    {
        var dest = Ax25Address.Parse(destination);
        var src = Ax25Address.Parse(sourceCallsign);

        var pathItems = string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var frame = new List<byte>(128);

        // Destination address (never the last address in the frame). The C
        // bits mark this a v2 command frame (dest 1 / src 0), matching what
        // modern TNCs emit for UI frames.
        frame.AddRange(EncodeAddress(dest, isLast: false, topBit: true));

        // Source address (last address only when there is no digipeater path)
        frame.AddRange(EncodeAddress(src, isLast: pathItems.Length == 0, topBit: false));

        // Digipeater path addresses
        for (var i = 0; i < pathItems.Length; i++)
            frame.AddRange(EncodeAddress(Ax25Address.Parse(pathItems[i]), isLast: i == pathItems.Length - 1));

        frame.Add(Ax25Frame.UiControl);
        frame.Add(Ax25Frame.NoLayer3Pid);

        frame.AddRange(Encoding.ASCII.GetBytes(aprsInfo));

        return [.. frame];
    }

    /// <summary>
    /// Re-encodes a decoded (possibly rewritten) frame back to raw AX.25
    /// bytes — used by the digipeater and third-party gating.
    /// </summary>
    public static byte[] Encode(Ax25Frame frame)
    {
        var bytes = new List<byte>(128);
        bytes.AddRange(EncodeAddress(frame.Destination, isLast: false, topBit: frame.DestCommandBit));
        bytes.AddRange(EncodeAddress(frame.Source, isLast: frame.Path.Count == 0, topBit: frame.SourceCommandBit));
        for (var i = 0; i < frame.Path.Count; i++)
            bytes.AddRange(EncodeAddress(frame.Path[i], isLast: i == frame.Path.Count - 1));
        bytes.Add(frame.Control);
        if (frame.Control2 is { } control2)
            bytes.Add(control2);
        if (frame.Pid is { } pid)
            bytes.Add(pid);
        bytes.AddRange(frame.Info);
        return [.. bytes];
    }

    /// <summary>
    /// Encodes a single AX.25 address field (7 bytes).  The top bit is emitted
    /// from <see cref="Ax25Address.HasBeenRepeated"/> — correct for digipeater
    /// path entries, where it is the H bit.
    /// </summary>
    public static byte[] EncodeAddress(Ax25Address address, bool isLast) =>
        EncodeAddress(address, isLast, topBit: address.HasBeenRepeated);

    /// <summary>
    /// Encodes a single AX.25 address field (7 bytes) with an explicit top
    /// bit — the C (command/response) bit on destination/source addresses,
    /// the H (has-been-repeated) bit on digipeater path entries.
    /// </summary>
    public static byte[] EncodeAddress(Ax25Address address, bool isLast, bool topBit)
    {
        // Pad or truncate to exactly 6 characters.
        var padded = address.Callsign.ToUpperInvariant().PadRight(6)[..6];

        var bytes = new byte[7];
        for (var i = 0; i < 6; i++)
            bytes[i] = (byte)((padded[i] & 0x7F) << 1);

        // SSID byte: bit 7 = C/H, bits 6-5 = reserved (11), bits 4-1 = SSID, bit 0 = end
        bytes[6] = (byte)(
            (topBit ? 0x80 : 0x00)
            | 0x60
            | ((address.Ssid & 0x0F) << 1)
            | (isLast ? 0x01 : 0x00));

        return bytes;
    }
}
