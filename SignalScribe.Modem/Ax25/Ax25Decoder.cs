namespace SignalScribe.Modem.Ax25;

/// <summary>
/// Decodes raw AX.25 frame bytes (as delivered by KISS or the HDLC deframer,
/// without flags or FCS) into an <see cref="Ax25Frame"/>.  Handles all frame
/// types: I, S (RR/RNR/REJ/SREJ), and U (UI, SABM, UA, DISC, DM, …) — only I
/// and UI frames carry a PID byte.
/// </summary>
public static class Ax25Decoder
{
    // AX.25 frame layout (after KISS/HDLC framing stripped):
    //   [0..6]   Destination (TOCALL)  — 7 bytes
    //   [7..13]  Source               — 7 bytes
    //   [14..]   Repeaters            — 7 bytes each; presence determined by end-bit
    //   Control field (1 byte; 2 bytes for I/S frames in modulo-128)
    //   PID byte  (I and UI frames only; 0xF0 for APRS)
    //   Information field (rest)
    //
    // Each address byte layout (byte 6 = SSID byte):
    //   Bit 7 : C bit on dest/src (command/response), H (has-been-repeated) on repeaters
    //   Bits 4–1 : SSID value 0–15
    //   Bit 0 : end-of-address-list flag

    /// <summary>Minimum frame size: 7 (dest) + 7 (src) + 1 (control) — a bare S frame.</summary>
    private const int MinFrameLength = 15;

    /// <summary>
    /// Attempts to decode <paramref name="data"/> as an AX.25 frame.
    /// <paramref name="extendedControl"/> selects modulo-128 interpretation of
    /// I/S control fields; callers without session context leave it false
    /// (correct for all U and UI frames — a mod-128 session owner re-decodes
    /// its own traffic with the hint set).  Returns <see langword="false"/>
    /// when the frame is too short for its mandatory fields.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out Ax25Frame frame, bool extendedControl = false)
    {
        frame = null!;

        if (data.Length < MinFrameLength)
            return false;

        // Destination and source: bit 7 of the SSID byte is the C (command/
        // response) bit here — only repeater entries use it as the H bit.
        var (destCall, destSsid, destCBit, _) = DecodeAddress(data, 0);
        var (srcCall, srcSsid, srcCBit, srcEnd) = DecodeAddress(data, 7);

        var path = new List<Ax25Address>();
        var pos = 14;
        var endBit = srcEnd;

        while (!endBit && pos + 7 <= data.Length)
        {
            var (repCall, repSsid, hBit, repEnd) = DecodeAddress(data, pos);
            path.Add(new Ax25Address(repCall, repSsid, hBit));
            endBit = repEnd;
            pos += 7;
        }

        if (pos >= data.Length)
            return false;

        var type = Ax25ControlField.Parse(data[pos..], extendedControl,
            out _, out _, out _, out var controlBytes);
        if (controlBytes == 0)
            return false;

        var control = data[pos];
        byte? control2 = controlBytes == 2 ? data[pos + 1] : null;
        pos += controlBytes;

        byte? pid = null;
        if (Ax25ControlField.HasPid(type))
        {
            // I and UI frames must carry a PID byte.
            if (pos >= data.Length)
                return false;
            pid = data[pos];
            pos += 1;
        }

        frame = new Ax25Frame
        {
            Destination = new Ax25Address(destCall, destSsid),
            Source = new Ax25Address(srcCall, srcSsid),
            Path = path,
            Control = control,
            Control2 = control2,
            Pid = pid,
            DestCommandBit = destCBit,
            SourceCommandBit = srcCBit,
            Info = pos < data.Length ? data[pos..].ToArray() : [],
        };
        return true;
    }

    private static (string Call, int Ssid, bool TopBit, bool EndBit) DecodeAddress(
        ReadOnlySpan<byte> buf, int offset)
    {
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < 6; i++)
            chars[i] = (char)(buf[offset + i] >> 1);
        var call = new string(chars).TrimEnd();

        var ssidByte = buf[offset + 6];
        var ssid = (ssidByte >> 1) & 0x0F;
        var topBit = (ssidByte & 0x80) != 0;
        var endBit = (ssidByte & 0x01) != 0;
        return (call, ssid, topBit, endBit);
    }
}
