namespace SignalScribe.Enums;

/// <summary>
/// What a transmission's modulation was measured to be, capture-side, from the discriminator.
///
/// This is a *measurement*, not a decode: naming the mode is what lets a channel be labelled and a
/// data frequency be understood rather than merely discarded. The definitive identification for the
/// digital voice modes comes later, from sync-pattern correlation in the per-mode framers — the
/// classifier's job is cheap triage, and <see cref="DigitalUnknown"/> is a legitimate answer.
/// </summary>
public enum DetectedMode
{
    /// <summary>No confident verdict. Too little signal, or nothing matched well enough to commit.</summary>
    Unknown = 0,

    /// <summary>Ordinary analog NBFM — continuous deviation, no symbol structure. The default on 2m.</summary>
    AnalogFm = 1,

    /// <summary>Bell 202 AFSK, 1200 baud: APRS and classic packet.</summary>
    Afsk1200 = 2,

    /// <summary>G3RUH 9600 baud direct FSK packet.</summary>
    Fsk9600 = 3,

    /// <summary>POCSAG paging, 2FSK at ±4.5 kHz.</summary>
    Pocsag = 4,

    /// <summary>FLEX paging, 2FSK/4FSK.</summary>
    Flex = 5,

    /// <summary>DMR Tier II: 4FSK, 4800 sym/s, ±1944/±648 Hz, 2-slot TDMA.</summary>
    Dmr = 6,

    /// <summary>D-STAR: GMSK, 4800 bps.</summary>
    DStar = 7,

    /// <summary>System Fusion: C4FM, 4800 sym/s.</summary>
    Ysf = 8,

    /// <summary>P25 Phase 1: C4FM, 4800 sym/s, ±1800/±600 Hz.</summary>
    P25Phase1 = 9,

    /// <summary>NXDN: 4FSK, 2400 (NXDN48) or 4800 (NXDN96) sym/s.</summary>
    Nxdn = 10,

    /// <summary>M17: C4FM with Codec 2 — the one digital voice mode that is open end to end.</summary>
    M17 = 11,

    /// <summary>
    /// Clearly not analog — discrete symbol levels were measured — but which mode it is could not be
    /// settled. Deliberately a first-class answer: guessing a specific mode from level structure alone
    /// is the same mistake as rounding a hum onto the nearest CTCSS tone.
    /// </summary>
    DigitalUnknown = 12,
}

/// <summary>Pure predicates over <see cref="DetectedMode"/>, kept separate from the capture code so they can be tested alone.</summary>
public static class DetectedModeExtensions
{
    /// <summary>
    /// Whether the mode is named specifically enough to say what a frequency *is*. This is what may
    /// rescue a transmission from the voice gate and bring a channel into existence, so it excludes
    /// all three ways of not knowing:
    /// <list type="bullet">
    /// <item><see cref="DetectedMode.AnalogFm"/> — an unmodulated carrier and a burst of noise both
    /// land here, and treating that as recognition would let every kerchunk and spur create a
    /// channel, which is exactly what the voice gate exists to prevent.</item>
    /// <item><see cref="DetectedMode.DigitalUnknown"/> — knowing a signal is digital is not knowing
    /// what it is. An APRS burst reads this way until the soft-TNC decodes a frame, and creating a
    /// channel on it would put 144.390 straight back to recording every packet forever, which is the
    /// failure <see cref="SignalScribe.Analysis.ChannelVoiceAudit"/> was written to undo.</item>
    /// <item><see cref="DetectedMode.Unknown"/> — no verdict at all.</item>
    /// </list>
    /// </summary>
    public static bool IsIdentified(this DetectedMode mode) =>
        mode is not (DetectedMode.Unknown or DetectedMode.AnalogFm or DetectedMode.DigitalUnknown);

    /// <summary>
    /// Whether the mode carries data rather than speech. Data channels are labelled and decoded to
    /// text but do not retain clips — the payload is the text, not the sound.
    /// </summary>
    public static bool IsData(this DetectedMode mode) => mode is
        DetectedMode.Afsk1200 or DetectedMode.Fsk9600 or DetectedMode.Pocsag or DetectedMode.Flex;

    /// <summary>Whether the mode is digital *voice* — framed, and vocodable into an ordinary clip.</summary>
    public static bool IsDigitalVoice(this DetectedMode mode) => mode is
        DetectedMode.Dmr or DetectedMode.DStar or DetectedMode.Ysf
        or DetectedMode.P25Phase1 or DetectedMode.Nxdn or DetectedMode.M17;
}
