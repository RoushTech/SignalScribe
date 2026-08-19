using System.Text;

namespace SignalScribe.Modem.Ax25;

/// <summary>
/// A decoded AX.25 frame: addresses, control/PID bytes, and the raw
/// information field.  Produced by <see cref="Ax25Decoder"/> and consumed by
/// the APRS pipeline and the LAPB session layer; <see cref="ToTnc2"/> renders
/// the canonical TNC2 string used throughout DireControl.
/// </summary>
public sealed class Ax25Frame
{
    /// <summary>AX.25 control byte for an Unnumbered Information (UI) frame.</summary>
    public const byte UiControl = 0x03;

    /// <summary>AX.25 PID byte for "no layer-3 protocol" (APRS).</summary>
    public const byte NoLayer3Pid = 0xF0;

    public required Ax25Address Destination { get; init; }
    public required Ax25Address Source { get; init; }
    public IReadOnlyList<Ax25Address> Path { get; init; } = [];
    public byte Control { get; init; } = UiControl;

    /// <summary>
    /// Second control byte when the frame was decoded as modulo-128
    /// (extended) — only I and S frames have one.  Null in modulo-8.
    /// </summary>
    public byte? Control2 { get; init; }

    /// <summary>
    /// Protocol ID.  Null for frame types that carry no PID (S frames and all
    /// U frames except UI).
    /// </summary>
    public byte? Pid { get; init; } = NoLayer3Pid;

    /// <summary>
    /// C bit (bit 7 of the SSID byte) on the destination address.  Together
    /// with <see cref="SourceCommandBit"/> this distinguishes AX.25 v2
    /// command frames (dest 1 / src 0) from response frames (dest 0 / src 1).
    /// </summary>
    public bool DestCommandBit { get; init; }

    /// <summary>C bit (bit 7 of the SSID byte) on the source address.</summary>
    public bool SourceCommandBit { get; init; }

    public byte[] Info { get; init; } = [];

    /// <summary>Frame type discriminated from the control field.</summary>
    public Ax25FrameType FrameType => ParseControl(out _, out _, out _);

    /// <summary>N(S) for I frames; null otherwise.</summary>
    public int? Ns
    {
        get
        {
            var type = ParseControl(out var ns, out _, out _);
            return type == Ax25FrameType.I ? ns : null;
        }
    }

    /// <summary>N(R) for I and S frames; null otherwise.</summary>
    public int? Nr
    {
        get
        {
            var type = ParseControl(out _, out var nr, out _);
            return type is Ax25FrameType.I or Ax25FrameType.RR or Ax25FrameType.RNR
                or Ax25FrameType.REJ or Ax25FrameType.SREJ ? nr : null;
        }
    }

    /// <summary>The P/F (poll/final) bit from the control field.</summary>
    public bool PollFinal
    {
        get
        {
            ParseControl(out _, out _, out var pf);
            return pf;
        }
    }

    private Ax25FrameType ParseControl(out int ns, out int nr, out bool pollFinal)
    {
        Span<byte> control = Control2 is { } c2 ? [Control, c2] : [Control];
        return Ax25ControlField.Parse(control, extended: Control2 is not null,
            out ns, out nr, out pollFinal, out _);
    }

    /// <summary>
    /// Renders the frame as a TNC2-format string —
    /// <c>SRC&gt;DEST,DIGI1,DIGI2*:info</c> — preserving the H bit on each
    /// digipeater as a <c>*</c> suffix.  The info field is decoded as ASCII to
    /// match what the rest of the pipeline stores and parses.
    /// </summary>
    public string ToTnc2()
    {
        var sb = new StringBuilder(64 + Info.Length);
        sb.Append(Source.ToString()).Append('>').Append(Destination.ToString());

        foreach (var digi in Path)
            sb.Append(',').Append(digi.ToString());

        sb.Append(':').Append(Encoding.ASCII.GetString(Info));
        return sb.ToString();
    }
}
