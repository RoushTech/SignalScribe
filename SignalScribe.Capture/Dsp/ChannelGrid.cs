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
    /// <summary>US/IARU channel step. Multiples of 2.5 kHz cover both 5 kHz and 12.5 kHz channel plans.</summary>
    public const long StepHz = 2_500;

    /// <summary>Corrections beyond half a bin mean the offset measurement is untrustworthy — keep the bin.</summary>
    public const double MaxCorrectionHz = 6_250;

    public static long Snap(long binFrequencyHz, double measuredOffsetHz)
    {
        if (double.IsNaN(measuredOffsetHz) || Math.Abs(measuredOffsetHz) > MaxCorrectionHz)
        {
            return SnapToStep(binFrequencyHz);
        }

        return SnapToStep(binFrequencyHz + (long)Math.Round(measuredOffsetHz));
    }

    private static long SnapToStep(long hz) => (long)Math.Round((double)hz / StepHz) * StepHz;
}
