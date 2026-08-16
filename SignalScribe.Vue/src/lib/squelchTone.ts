/** How a CTCSS tone or DCS code reads to an operator. */
export function toneLabel(ctcssHz: number | null, dcsCode: number | null): string | null {
  if (ctcssHz) return `${ctcssHz.toFixed(1)} Hz`;
  if (dcsCode) return `D${String(dcsCode).padStart(3, "0")}`;
  return null;
}

/** Spelled out, for a tooltip. */
export function toneName(ctcssHz: number | null, dcsCode: number | null): string | null {
  if (ctcssHz) return `CTCSS ${ctcssHz.toFixed(1)} Hz`;
  if (dcsCode) return `DCS ${String(dcsCode).padStart(3, "0")}`;
  return null;
}

/**
 * A channel's tone: what the operator set, or failing that what we have heard on it. Measured
 * values are marked, because a learned tone is evidence rather than configuration.
 */
export function channelTone(c: {
  ctcssToneHz: number | null;
  measuredCtcssToneHz: number | null;
  measuredDcsCode: number | null;
}): { label: string; measured: boolean; detail: string } | null {
  if (c.ctcssToneHz) {
    return {
      label: `${c.ctcssToneHz.toFixed(1)} Hz`,
      measured: false,
      detail: `CTCSS ${c.ctcssToneHz.toFixed(1)} Hz, set by you on this channel.`,
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
