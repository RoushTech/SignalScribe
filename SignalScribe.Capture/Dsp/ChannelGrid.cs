namespace SignalScribe.Capture.Dsp;

/// <summary>
/// Maps a channelizer bin plus its measured carrier offset onto the real channel grid.
///
/// The filterbank grid (12.5 kHz, chosen so each channel is wide enough for NBFM) does not line up
/// with the 5 kHz grid amateur channels actually use: 146.790 MHz lands 2.5 kHz off bin centre. The
/// demodulator measures that offset as discriminator DC, so the true carrier is bin + offset —
/// snapped to the channel grid to absorb transmitter and receiver frequency error (a few ppm at
/// 146 MHz is a few hundred Hz).
/// </summary>
public static class ChannelGrid
{
    /// <summary>
    /// US/IARU channel step. Every allocation on 2m sits on a multiple of 5 kHz — repeater pairs on
    /// 15/20 kHz spacing, simplex on 20 kHz, APRS on 144.390 — so 5 kHz is the real grid. A 2.5 kHz
    /// step also accepts the odd 12.5 kHz-plan frequency, but the extra grid points it allows are
    /// precisely the filterbank's own bin centres, which is where an off-frequency transmitter gets
    /// wrongly parked: a digipeater measured 1.75 kHz low reported as 144.3875 (a bin centre) rather
    /// than 144.390. Snapping on 5 kHz absorbs transmitter error up to half a channel instead.
    /// </summary>
    public const long StepHz = 5_000;

    /// <summary>Corrections beyond half a bin mean the offset measurement is untrustworthy — keep the bin.</summary>
    public const double MaxCorrectionHz = 6_250;

    public static long Snap(long binFrequencyHz, double measuredOffsetHz)
    {
        if (double.IsNaN(measuredOffsetHz) || Math.Abs(measuredOffsetHz) > MaxCorrectionHz)
        {
            // No trustworthy offset: report the bin untouched. Snapping it anyway would be a coin
            // flip — a bin centre falls exactly halfway between two 5 kHz channels — and inventing
            // a precise-looking frequency we did not measure is worse than an obvious bin centre.
            return binFrequencyHz;
        }

        return SnapToStep(binFrequencyHz + (long)Math.Round(measuredOffsetHz));
    }

    private static long SnapToStep(long hz) => (long)Math.Round((double)hz / StepHz) * StepHz;
}
