namespace SignalScribe.Modem.Ax25;

/// <summary>
/// A single AX.25 address field: base callsign, SSID, and the
/// has-been-repeated (H) bit that is meaningful for digipeater entries.
/// </summary>
public readonly record struct Ax25Address(string Callsign, int Ssid, bool HasBeenRepeated = false)
{
    /// <summary>
    /// Formats as TNC2: <c>CALL</c> or <c>CALL-N</c>, with a <c>*</c> suffix
    /// when the H bit is set.
    /// </summary>
    public override string ToString()
    {
        var s = Ssid > 0 ? $"{Callsign}-{Ssid}" : Callsign;
        return HasBeenRepeated ? s + "*" : s;
    }

    /// <summary>
    /// Parses a <c>CALL</c> / <c>CALL-N</c> string (optionally with a trailing
    /// <c>*</c> marking the H bit) into its parts.  A non-numeric suffix after
    /// the dash yields SSID 0, matching historical DireControl behaviour.
    /// </summary>
    public static Ax25Address Parse(string raw)
    {
        var hBit = raw.EndsWith('*');
        if (hBit)
            raw = raw[..^1];

        var parts = raw.Split('-', 2);
        var ssid = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        return new Ax25Address(parts[0], ssid, hBit);
    }
}
