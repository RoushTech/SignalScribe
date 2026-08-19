/**
 * Mirrors the status strings computed in SignalScribe.Api TransmissionMapper.Status.
 *
 * The API already worked out *why* a transmission has no text, so a view must never invent its own
 * reason: a Fusion clip is "not decoded" because no vocoder is wired up, which is a settled end
 * state, and calling that "not transcribed yet" promises something that is never coming.
 */
export const TRANSMISSION_STATUSES: Record<
  string,
  { text: string; detail: string; color: string }
> = {
  double: {
    text: "Two stations at once — nothing readable",
    detail: "A heterodyne inside the voice band: two carriers on one channel beating together.",
    color: "warning",
  },
  transcribed: {
    text: "Transcribed",
    detail: "Speech was heard and a transcript is on this clip.",
    color: "success",
  },
  processing: {
    text: "Waiting to be transcribed",
    detail: "Voice was measured on this clip and it is queued for the transcription worker.",
    color: "info",
  },
  "no speech": {
    text: "No speech",
    detail: "Either too little voice to be worth transcribing, or transcribed and nothing was said.",
    color: "grey",
  },
  "not decoded": {
    text: "Digital voice — no vocoder",
    detail:
      "A digital voice mode, so nothing is coming: the vocoder is not wired up and no header was " +
      "recovered either. This is a finished state, not a pending one.",
    color: "purple",
  },
  identified: {
    text: "Digital voice — header decoded, no vocoder",
    detail:
      "The mode's header was decoded, so we know who was calling whom — but the speech itself " +
      "needs a vocoder that is not wired up yet.",
    color: "purple",
  },
  decoded: {
    text: "Data frame decoded",
    detail: "A CRC-valid packet. The raw frame is the record; the reading beside it is derived.",
    color: "success",
  },
  data: {
    text: "Data burst, nothing decoded",
    detail: "Measured as data rather than speech, but no frame came out of it.",
    color: "info",
  },
};

/** The phrase to show where a transcript would have gone. Never "not transcribed yet". */
export function statusText(status: string): string {
  return TRANSMISSION_STATUSES[status]?.text ?? status;
}

/** The longer read that hangs off the status chip on hover. */
export function statusDetail(status: string): string {
  return TRANSMISSION_STATUSES[status]?.detail ?? "";
}

export function statusColor(status: string): string {
  return TRANSMISSION_STATUSES[status]?.color ?? "grey";
}
