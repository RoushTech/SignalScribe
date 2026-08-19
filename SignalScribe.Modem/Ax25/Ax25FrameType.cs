namespace SignalScribe.Modem.Ax25;

/// <summary>
/// AX.25 frame type, discriminated from the control field. I and UI frames
/// carry a PID byte; all other types do not.
/// </summary>
public enum Ax25FrameType
{
    Unknown = 0,

    /// <summary>Information frame (connected mode, sequenced).</summary>
    I,

    // Supervisory frames
    RR,
    RNR,
    REJ,
    SREJ,

    // Unnumbered frames
    UI,
    SABM,
    SABME,
    DISC,
    DM,
    UA,
    FRMR,
    XID,
    TEST,
}
