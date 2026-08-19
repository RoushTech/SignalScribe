# SignalScribe.Modem — MIT, not GPL

**This subdirectory is MIT licensed** (see `LICENSE`), unlike the rest of SignalScribe, which is
GPLv3. That is deliberate and load-bearing:

- The code is vendored from [RoushTech/DireControl](https://github.com/RoushTech/DireControl)
  (`stack` branch, `DireControl.Modem/`), which is MIT and has the same copyright holder.
- MIT flows into GPL, but never back. Keeping this subtree permissive is what lets fixes made here —
  and this project puts the demodulator in front of far more real air than DireControl does — return
  to DireControl.

**Never copy GPL code into this directory.** Anything ported from SDRTrunk, DSDcc, OP25, JMBE or
multimon-ng belongs in `SignalScribe.Capture/`, not here.

## What this is

The receive half of a soft TNC: audio in, CRC-valid AX.25 frames out.

```
Dsp/    AfskDemodulator — bandpass pre-filter → mark/space quadrature correlators
        → per-tone AGC → comparator → PLL bit-clock recovery
        ToneCorrelator, FirFilter, Agc, BitClockPll, DemodProfile
Hdlc/   NrziDecoder, HdlcDeframer (flag detection, bit de-stuffing), Crc16Ccitt
Ax25/   Ax25Decoder → Ax25Frame (addresses, control, PID, info) → ToTnc2()
        PacketReceiver — N parallel demodulator profiles, duplicates collapsed
```

## What was left behind

Upstream's `AfskReceiver` was replaced by `PacketReceiver`, which drops the spectrum analyser and
input-level metering — SignalScribe has its own waterfall and level tracking, and the modem should
not carry a second set. The transmit path (`Ax25Encoder`, `Ptt/`), the connected-mode LAPB state
machine, ALSA capture, and the KISS/AGWPE server framing were not vendored: SignalScribe is
receive-only and supplies its own samples.

## Divergence

Namespaces were rewritten `DireControl.Modem.*` → `SignalScribe.Modem.*`. Otherwise the vendored
files are upstream's, so a diff against DireControl stays readable.
