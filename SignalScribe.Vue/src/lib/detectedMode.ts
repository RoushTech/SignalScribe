/** Mirrors SignalScribe.Enums.DetectedMode. */
export enum DetectedMode {
  Unknown = 0,
  AnalogFm = 1,
  Afsk1200 = 2,
  Fsk9600 = 3,
  Pocsag = 4,
  Flex = 5,
  Dmr = 6,
  DStar = 7,
  Ysf = 8,
  P25Phase1 = 9,
  Nxdn = 10,
  M17 = 11,
  DigitalUnknown = 12,
}

/** Short label for the chip, the longer read on hover, and how loudly to say it. */
export const DETECTED_MODES: Record<DetectedMode, { label: string; detail: string; color: string }> = {
  [DetectedMode.Unknown]: {
    label: "unknown",
    detail: "Not enough signal to tell what modulation this was.",
    color: "surface-variant",
  },
  [DetectedMode.AnalogFm]: {
    label: "FM",
    detail: "Ordinary analog narrowband FM — the usual thing on 2m.",
    color: "surface-variant",
  },
  [DetectedMode.Afsk1200]: {
    label: "APRS",
    detail: "1200 baud packet. Confirmed by decoding an AX.25 frame, not guessed.",
    color: "info",
  },
  [DetectedMode.Fsk9600]: {
    label: "9600 packet",
    detail: "9600 baud G3RUH packet data.",
    color: "info",
  },
  [DetectedMode.Pocsag]: {
    label: "POCSAG",
    detail: "Pager traffic. Two-level FSK with a wide ±4.5 kHz shift.",
    color: "info",
  },
  [DetectedMode.Flex]: {
    label: "FLEX",
    detail: "Motorola FLEX paging.",
    color: "info",
  },
  [DetectedMode.Dmr]: {
    label: "DMR",
    detail: "Four-level C4FM on DMR's ±1944/±648 Hz plan, two slots of TDMA.",
    color: "purple",
  },
  [DetectedMode.DStar]: {
    label: "D-STAR",
    detail: "GMSK at 4800 bps. The header carries callsigns in plain text.",
    color: "purple",
  },
  [DetectedMode.Ysf]: {
    label: "Fusion",
    detail: "Yaesu System Fusion, C4FM.",
    color: "purple",
  },
  [DetectedMode.P25Phase1]: {
    label: "P25",
    detail: "P25 Phase 1, C4FM on the ±1800/±600 Hz plan.",
    color: "purple",
  },
  [DetectedMode.Nxdn]: {
    label: "NXDN",
    detail: "NXDN four-level FSK.",
    color: "purple",
  },
  [DetectedMode.M17]: {
    label: "M17",
    detail: "M17 — the open digital voice mode, C4FM with Codec 2.",
    color: "purple",
  },
  [DetectedMode.DigitalUnknown]: {
    label: "digital",
    detail:
      "Discrete symbol levels, so something is sending data — but not a mode we can name yet. " +
      "Several modes share a deviation plan, so naming one from the levels alone would be a guess.",
    color: "warning",
  },
};

export function modeLabel(mode: DetectedMode): string {
  return DETECTED_MODES[mode]?.label ?? "unknown";
}

export function modeDetail(mode: DetectedMode): string {
  return DETECTED_MODES[mode]?.detail ?? "";
}

export function modeColor(mode: DetectedMode): string {
  return DETECTED_MODES[mode]?.color ?? "surface-variant";
}

/** True when the mode names the frequency specifically — mirrors DetectedModeExtensions.IsIdentified. */
export function isIdentified(mode: DetectedMode): boolean {
  return (
    mode !== DetectedMode.Unknown &&
    mode !== DetectedMode.AnalogFm &&
    mode !== DetectedMode.DigitalUnknown
  );
}

/** Modes worth putting a chip on. Analog FM is the default and would be noise on every row. */
export function isWorthShowing(mode: DetectedMode): boolean {
  return mode !== DetectedMode.Unknown && mode !== DetectedMode.AnalogFm;
}

/** Everything the <ModeChip> component needs to render one tag. */
export interface ModeChipSpec {
  label: string;
  detail: string;
  color: string;
  icon: string;
  variant: "tonal" | "outlined";
}

function plain(mode: DetectedMode): ModeChipSpec {
  return {
    label: modeLabel(mode),
    detail: modeDetail(mode),
    color: modeColor(mode),
    icon: isIdentified(mode) ? "mdi-waveform" : "mdi-help-rhombus-outline",
    variant: "tonal",
  };
}

/**
 * Modulation measured on one transmission, and whether it is what this channel normally carries.
 * A mode that differs from the channel's usual one is the interesting case, exactly as with tones:
 * a different system sharing the frequency, or someone set to the wrong mode.
 */
export function transmissionModeChip(mode: DetectedMode, channelMode: DetectedMode | null): ModeChipSpec | null {
  if (!isWorthShowing(mode)) return null;

  if (channelMode != null && channelMode !== mode && isIdentified(mode) && isIdentified(channelMode)) {
    return {
      label: modeLabel(mode),
      color: "warning",
      icon: "mdi-swap-horizontal",
      variant: "tonal",
      detail: `${modeDetail(mode)} This channel normally carries ${modeLabel(channelMode)}, so this is either a different system on the same frequency or someone set to the wrong mode.`,
    };
  }

  return plain(mode);
}

/**
 * What a channel carries. An operator-set modulation wins over the measured one and shows solid;
 * a measured mode shows outlined with an ear, the same convention the tone column already uses.
 */
export function channelModeChip(
  declared: DetectedMode | null,
  measured: DetectedMode | null,
): ModeChipSpec | null {
  if (declared != null) {
    return {
      label: modeLabel(declared),
      color: modeColor(declared),
      detail: `Set by you as ${modeLabel(declared)}. ${modeDetail(declared)}`,
      icon: "mdi-waveform",
      variant: "tonal",
    };
  }

  if (measured == null || !isWorthShowing(measured)) return null;

  return {
    label: modeLabel(measured),
    color: modeColor(measured),
    detail: `Measured from the air. ${modeDetail(measured)}`,
    icon: "mdi-ear-hearing",
    variant: "outlined",
  };
}
