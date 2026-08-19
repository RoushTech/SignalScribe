/**
 * Standard CTCSS tone table, 67.0–254.1 Hz. Anything off this list is a measurement, not a
 * setting — a radio cannot be told to send a tone that is not on it.
 */
export const CTCSS_TONES = [
  67.0, 69.3, 71.9, 74.4, 77.0, 79.7, 82.5, 85.4, 88.5, 91.5, 94.8, 97.4, 100.0, 103.5, 107.2, 110.9, 114.8, 118.8,
  123.0, 127.3, 131.8, 136.5, 141.3, 146.2, 151.4, 156.7, 159.8, 162.2, 165.5, 167.9, 171.3, 173.8, 177.3, 179.9,
  183.5, 186.2, 189.9, 192.8, 196.6, 199.5, 203.5, 206.5, 210.7, 213.8, 218.1, 221.3, 225.7, 229.1, 233.6, 237.1,
  241.8, 245.5, 250.3, 254.1,
];

/**
 * Standard DCS codes. Operators quote them in octal and always three digits wide, so 23 is written
 * 023 — the stored number is the digits, not their octal value.
 */
export const DCS_CODES = [
  23, 25, 26, 31, 32, 36, 43, 47, 51, 53, 54, 65, 71, 72, 73, 74, 114, 115, 116, 122, 125, 131, 132, 134, 143, 145,
  152, 155, 156, 162, 165, 172, 174, 205, 212, 223, 225, 226, 243, 244, 245, 246, 251, 252, 255, 261, 263, 265, 266,
  271, 274, 306, 311, 315, 325, 331, 332, 343, 346, 351, 356, 364, 365, 371, 411, 412, 413, 423, 431, 432, 445, 446,
  452, 454, 455, 462, 464, 465, 466, 503, 506, 516, 523, 526, 532, 546, 565, 606, 612, 624, 627, 631, 632, 654, 662,
  664, 703, 712, 723, 731, 732, 734, 743, 754,
];

/** Zero-padded to three digits, the way operators write a DCS code. */
export function formatDcs(code: number): string {
  return String(code).padStart(3, "0");
}

/** How a CTCSS tone or DCS code reads to an operator. */
export function toneLabel(ctcssHz: number | null, dcsCode: number | null): string | null {
  if (ctcssHz) return `${ctcssHz.toFixed(1)} Hz`;
  if (dcsCode) return `DCS ${formatDcs(dcsCode)}`;
  return null;
}

/** Spelled out, for a tooltip. */
export function toneName(ctcssHz: number | null, dcsCode: number | null): string | null {
  if (ctcssHz) return `CTCSS ${ctcssHz.toFixed(1)} Hz`;
  if (dcsCode) return `DCS ${formatDcs(dcsCode)}`;
  return null;
}

/**
 * A channel's tone: what the operator set, or failing that what we have heard on it. Measured
 * values are marked, because a learned tone is evidence rather than configuration.
 */
export function channelTone(c: {
  ctcssToneHz: number | null;
  dcsCode: number | null;
  measuredCtcssToneHz: number | null;
  measuredDcsCode: number | null;
}): { label: string; measured: boolean; detail: string } | null {
  const configured = toneName(c.ctcssToneHz, c.dcsCode);
  if (configured) {
    return {
      label: toneLabel(c.ctcssToneHz, c.dcsCode)!,
      measured: false,
      detail: `${configured}, set by you on this channel.`,
    };
  }

  const heard = toneName(c.measuredCtcssToneHz, c.measuredDcsCode);
  if (!heard) return null;

  return {
    label: toneLabel(c.measuredCtcssToneHz, c.measuredDcsCode)!,
    measured: true,
    detail: `${heard}, heard on this channel's traffic rather than configured. Two repeaters often share an output frequency and the tone is what tells them apart.`,
  };
}

/** The squelch reference, in dBFS. Null means capture has never had a quiet moment to learn one. */
export function formatNoiseFloor(dbfs: number | null): string {
  return dbfs == null ? "—" : `${dbfs.toFixed(1)} dBFS`;
}
