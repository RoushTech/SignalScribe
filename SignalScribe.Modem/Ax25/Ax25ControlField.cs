namespace SignalScribe.Modem.Ax25;

/// <summary>
/// Parses and builds AX.25 control fields for both modulo-8 (1 byte) and
/// modulo-128 "extended" (2 bytes for I/S frames) operation.  U frames are
/// always a single control byte regardless of modulus.
/// </summary>
public static class Ax25ControlField
{
    // U-frame opcodes with the P/F bit (0x10) masked out.
    private const byte UMask = 0xEF;
    private const byte USabm = 0x2F;
    private const byte USabme = 0x6F;
    private const byte UDisc = 0x43;
    private const byte UDm = 0x0F;
    private const byte UUa = 0x63;
    private const byte UFrmr = 0x87;
    private const byte UUi = 0x03;
    private const byte UXid = 0xAF;
    private const byte UTest = 0xE3;

    /// <summary>Whether frames of <paramref name="type"/> carry a PID byte.</summary>
    public static bool HasPid(Ax25FrameType type) => type is Ax25FrameType.I or Ax25FrameType.UI;

    /// <summary>
    /// Parses the control field at the start of <paramref name="data"/>.
    /// <paramref name="extended"/> selects modulo-128 interpretation for I and
    /// S frames (2-byte control); U frames are unaffected by it.  Returns
    /// <see cref="Ax25FrameType.Unknown"/> for unrecognised U opcodes and when
    /// an extended I/S frame is truncated before its second control byte.
    /// </summary>
    /// <param name="ns">N(S) for I frames, otherwise 0.</param>
    /// <param name="nr">N(R) for I and S frames, otherwise 0.</param>
    public static Ax25FrameType Parse(
        ReadOnlySpan<byte> data,
        bool extended,
        out int ns,
        out int nr,
        out bool pollFinal,
        out int bytesConsumed)
    {
        ns = 0;
        nr = 0;
        pollFinal = false;
        bytesConsumed = 0;

        if (data.IsEmpty)
            return Ax25FrameType.Unknown;

        var c0 = data[0];

        // U frame: bits 1-0 == 11. Always one control byte.
        if ((c0 & 0x03) == 0x03)
        {
            bytesConsumed = 1;
            pollFinal = (c0 & 0x10) != 0;
            return (byte)(c0 & UMask) switch
            {
                USabm => Ax25FrameType.SABM,
                USabme => Ax25FrameType.SABME,
                UDisc => Ax25FrameType.DISC,
                UDm => Ax25FrameType.DM,
                UUa => Ax25FrameType.UA,
                UFrmr => Ax25FrameType.FRMR,
                UUi => Ax25FrameType.UI,
                UXid => Ax25FrameType.XID,
                UTest => Ax25FrameType.TEST,
                _ => Ax25FrameType.Unknown,
            };
        }

        // S frame: bits 1-0 == 01.
        if ((c0 & 0x03) == 0x01)
        {
            var sType = ((c0 >> 2) & 0x03) switch
            {
                0 => Ax25FrameType.RR,
                1 => Ax25FrameType.RNR,
                2 => Ax25FrameType.REJ,
                _ => Ax25FrameType.SREJ,
            };

            if (extended)
            {
                if (data.Length < 2)
                    return Ax25FrameType.Unknown;
                bytesConsumed = 2;
                nr = data[1] >> 1;
                pollFinal = (data[1] & 0x01) != 0;
            }
            else
            {
                bytesConsumed = 1;
                nr = c0 >> 5;
                pollFinal = (c0 & 0x10) != 0;
            }
            return sType;
        }

        // I frame: bit 0 == 0.
        if (extended)
        {
            if (data.Length < 2)
                return Ax25FrameType.Unknown;
            bytesConsumed = 2;
            ns = c0 >> 1;
            nr = data[1] >> 1;
            pollFinal = (data[1] & 0x01) != 0;
        }
        else
        {
            bytesConsumed = 1;
            ns = (c0 >> 1) & 0x07;
            nr = c0 >> 5;
            pollFinal = (c0 & 0x10) != 0;
        }
        return Ax25FrameType.I;
    }

    /// <summary>
    /// Builds the control field bytes for a frame.  U frames are always one
    /// byte; I and S frames are two bytes when <paramref name="extended"/>.
    /// </summary>
    public static byte[] Build(Ax25FrameType type, bool extended, int ns, int nr, bool pollFinal)
    {
        var pf = pollFinal ? 1 : 0;

        switch (type)
        {
            case Ax25FrameType.I:
                if (extended)
                    return [(byte)((ns & 0x7F) << 1), (byte)(((nr & 0x7F) << 1) | pf)];
                return [(byte)(((nr & 0x07) << 5) | (pf << 4) | ((ns & 0x07) << 1))];

            case Ax25FrameType.RR:
            case Ax25FrameType.RNR:
            case Ax25FrameType.REJ:
            case Ax25FrameType.SREJ:
                var ss = type switch
                {
                    Ax25FrameType.RR => 0,
                    Ax25FrameType.RNR => 1,
                    Ax25FrameType.REJ => 2,
                    _ => 3,
                };
                if (extended)
                    return [(byte)(0x01 | (ss << 2)), (byte)(((nr & 0x7F) << 1) | pf)];
                return [(byte)(((nr & 0x07) << 5) | (pf << 4) | (ss << 2) | 0x01)];

            default:
                var opcode = type switch
                {
                    Ax25FrameType.SABM => USabm,
                    Ax25FrameType.SABME => USabme,
                    Ax25FrameType.DISC => UDisc,
                    Ax25FrameType.DM => UDm,
                    Ax25FrameType.UA => UUa,
                    Ax25FrameType.FRMR => UFrmr,
                    Ax25FrameType.UI => UUi,
                    Ax25FrameType.XID => UXid,
                    Ax25FrameType.TEST => UTest,
                    _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Cannot build a control field for this frame type."),
                };
                return [(byte)(opcode | (pf << 4))];
        }
    }
}
