/** Mirrors SignalScribe.Enums.DiscardReason. */
export enum DiscardReason {
  Unknown = 0,
  TooShort = 1,
  FrontEndOverload = 2,
  SteadyTone = 3,
  OutsideSpeechBand = 4,
  TooFastForSpeech = 5,
  NotEnoughVoice = 6,
  NoSyllableRhythm = 7,
  DigitalNotIdentified = 8,
}

/** Short label for the chip, and the longer read that hangs off it on hover. */
export const DISCARD_REASONS: Record<DiscardReason, { label: string; detail: string }> = {
  [DiscardReason.Unknown]: {
    label: "unknown",
    detail: "It failed the voice check, but none of the individual tests stands out.",
  },
  [DiscardReason.TooShort]: {
    label: "too short",
    detail: "Under 200ms of signal. A click, a stray keyup, or splatter from the channel next door.",
  },
  [DiscardReason.FrontEndOverload]: {
    label: "front end overload",
    detail: "The receiver was clipping, so nothing it heard was real. Usually a transmitter close by.",
  },
  [DiscardReason.SteadyTone]: {
    label: "steady tone",
    detail: "One constant tone the whole way through. A carrier, a heterodyne, or a stuck mic.",
  },
  [DiscardReason.OutsideSpeechBand]: {
    label: "not speech",
    detail: "Hardly any energy where a voice would be. Data, noise, or a spur.",
  },
  [DiscardReason.TooFastForSpeech]: {
    label: "too fast",
    detail: "Changing far quicker than anyone can talk. Packet or APRS, most likely.",
  },
  [DiscardReason.NotEnoughVoice]: {
    label: "barely any voice",
    detail: "Sounds like someone talking, but under 800ms of it. Too little to bother transcribing.",
  },
  [DiscardReason.NoSyllableRhythm]: {
    label: "flat",
    detail: "The level hardly moves. Speech rises and falls between syllables; this does not.",
  },
  [DiscardReason.DigitalNotIdentified]: {
    label: "digital",
    detail:
      "Discrete symbol levels, so something is sending data here — but not a mode we can name, " +
      "and that alone is not enough to start recording a frequency forever.",
  },
};

export function discardLabel(reason: DiscardReason): string {
  return (DISCARD_REASONS[reason] ?? DISCARD_REASONS[DiscardReason.Unknown]).label;
}

export function discardDetail(reason: DiscardReason): string {
  return (DISCARD_REASONS[reason] ?? DISCARD_REASONS[DiscardReason.Unknown]).detail;
}
