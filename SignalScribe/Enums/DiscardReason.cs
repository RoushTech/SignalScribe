namespace SignalScribe.Enums;

/// <summary>Why a clip was rejected instead of becoming a transmission. Set capture-side, where the audio measurements are.</summary>
public enum DiscardReason
{
    /// <summary>Rejected, but none of the specific tests explains it.</summary>
    Unknown = 0,

    /// <summary>Barely any signal was present before the gate shut again.</summary>
    TooShort = 1,

    /// <summary>The receiver's front end was clipping, so nothing measured was real.</summary>
    FrontEndOverload = 2,

    /// <summary>A constant tone rather than speech.</summary>
    SteadyTone = 3,

    /// <summary>Almost no energy in the part of the spectrum voice occupies.</summary>
    OutsideSpeechBand = 4,

    /// <summary>Modulating far faster than anyone can speak.</summary>
    TooFastForSpeech = 5,

    /// <summary>Some voice-like audio, but not enough of it.</summary>
    NotEnoughVoice = 6,

    /// <summary>The level barely moves, so there is no syllable rhythm to find.</summary>
    NoSyllableRhythm = 7,

    /// <summary>
    /// Discrete symbol levels, so something is transmitting data — but not a mode we can name, and
    /// knowing only that a signal is digital is not grounds for creating a channel. Recording every
    /// burst on an unidentified data frequency is precisely the failure
    /// <see cref="SignalScribe.Analysis.ChannelVoiceAudit"/> exists to undo.
    /// </summary>
    DigitalNotIdentified = 8,
}
