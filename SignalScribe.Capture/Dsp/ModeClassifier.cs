using SignalScribe.Enums;

namespace SignalScribe.Capture.Dsp;

/// <summary>Diagnostics from the last classification — the measurements behind the verdict, for tuning against real air.</summary>
/// <param name="Levels">Discrete deviation levels found: 4 for C4FM, 2 for 2FSK/GMSK, 1 for analog or noise.</param>
/// <param name="OuterDeviationHz">Magnitude of the outermost symbol level, which is what names a 4FSK mode.</param>
/// <param name="InnerDeviationHz">Magnitude of the inner level pair; about a third of the outer on real C4FM.</param>
/// <param name="Prominence">How deep the valley between levels is, 0–1. Discrete symbols cut deep; a speech hump does not.</param>
/// <param name="Symmetry">Level-plan symmetry about zero, 0–1. Real FSK is symmetric; an accidental pair of humps is not.</param>
public readonly record struct ModeScore(
    int Levels,
    double OuterDeviationHz,
    double InnerDeviationHz,
    double Prominence,
    double Symmetry);

/// <summary>
/// Names the modulation of a transmission from the FM discriminator, so a channel can be labelled by
/// what it actually carries instead of being discarded for not sounding like speech.
///
/// Fed the discriminator in Hz with the carrier offset already removed, ahead of de-emphasis and the
/// soft limiter — both of those exist to make speech pleasant and destroy exactly the level structure
/// this reads. Same tap discipline as <see cref="SubaudibleDetector"/>, one stage earlier.
///
/// The method rests on one observation: a digital signal spends its time at a *small set of discrete
/// deviations*, and speech does not. Histogram the deviation over a whole transmission and C4FM shows
/// four peaks, 2FSK and GMSK show two, and speech or noise shows a single hump at zero. Where the
/// peaks sit then names the mode, because the deviations are specified — DMR is ±1944/±648 Hz, P25 and
/// YSF are ±1800/±600, POCSAG is ±4500.
///
/// What separates real symbol levels from ripple on a speech hump is **prominence**: the valley
/// between two genuine levels drops away, because the signal crosses that deviation only in transit.
/// Counting local maxima alone finds four peaks in Gaussian noise.
///
/// This is triage, not decoding, and it deliberately stops short of guessing:
/// <list type="bullet">
/// <item>Two-level modes separate cleanly, because D-STAR's ±1.2 kHz and POCSAG's ±4.5 kHz are nearly
/// four times apart and no plausible filtering or transmitter error closes that.</item>
/// <item>Four-level modes are reported only as <see cref="DetectedMode.DigitalUnknown"/>. DMR and P25
/// sit 8% apart, which is the same order as the channel filter's compression and the transmitter's
/// own deviation error — see <c>FourLevelMode</c>. Their sync patterns separate them; deviation does
/// not.</item>
/// <item>AFSK packet is not detected here at all. A CRC-valid AX.25 frame out of the soft-TNC is a
/// definitive identification, and no statistic can compete with that; a Bell 202 burst reads as an
/// anonymous two-level signal here until the decoder confirms it.</item>
/// </list>
/// Committing to a guess would be the same error as rounding a mains hum onto the nearest CTCSS tone.
/// </summary>
public sealed class ModeClassifier
{
    /// <summary>
    /// Below this there is not enough signal for the histogram to have shape. Short by design: an APRS
    /// packet is often under a second, and a DMR burst shorter still.
    /// </summary>
    public const double MinSeconds = 0.25;

    /// <summary>Histogram span, ±Hz. Matches the channel filter's passband — deviation beyond this is not ours anyway.</summary>
    private const double HistRangeHz = 6_400;

    private const int HistBins = 128;   // 100 Hz per bin: enough to separate ±648 from ±1944

    private const double BinWidthHz = 2 * HistRangeHz / HistBins;

    /// <summary>A level must reach this share of the busiest one to be considered at all.</summary>
    private const double MinPeakShare = 0.30;

    /// <summary>
    /// The valley between two levels must fall to this share of the smaller of them. Tuned to sit
    /// clearly below what ripple on a Gaussian hump produces (which barely dips at all) and clearly
    /// above the near-empty gap between real C4FM symbol levels.
    /// </summary>
    private const double MaxValleyShare = 0.72;

    /// <summary>
    /// Deviation fed before this is thrown away, because it is not yet referenced to the carrier.
    ///
    /// The demodulator's DC tracker is an EWMA with a 200 ms time constant, seeded from the very
    /// first sample after the gate opens — which on a digital signal is whichever symbol happened to
    /// be transmitting. It then crawls toward the true carrier over roughly three time constants, and
    /// every sample along the way is offset by a different decaying amount. That does not shift the
    /// histogram, it *smears* it: measured end to end, four clean symbol levels turned into one hump
    /// and DMR classified as analog. Waiting out the settle costs half a second of a transmission
    /// that is usually several seconds long.
    /// </summary>
    private const double WarmupSeconds = 0.35;

    private readonly double _rate;

    private readonly long[] _hist = new long[HistBins];

    private long _samples;

    private long _skipped;

    private readonly long _warmupSamples;

    public ModeClassifier(double inputSampleRate)
    {
        _rate = inputSampleRate;
        _warmupSamples = (long)(inputSampleRate * WarmupSeconds);
    }

    /// <summary>Seconds of usable signal seen, excluding the warmup. Below <see cref="MinSeconds"/> no verdict is offered.</summary>
    public double Seconds => _samples / _rate;

    /// <summary>Measurements behind the last <see cref="Classify"/> call.</summary>
    public ModeScore LastScore { get; private set; }

    /// <summary>
    /// Feed one discriminator sample, in Hz, with the slow carrier offset already subtracted. Only
    /// call while the squelch says signal is present: the tail and hang time carry noise that averages
    /// to zero, which fills the middle of the histogram and buries the symbol levels.
    /// </summary>
    public void Feed(float deviationHz)
    {
        if (_skipped < _warmupSamples)
        {
            _skipped++;
            return;
        }

        _samples++;
        var bin = (int)((deviationHz + HistRangeHz) / BinWidthHz);
        if ((uint)bin < HistBins)
        {
            _hist[bin]++;
        }
    }

    /// <summary>
    /// The modulation present, or <see cref="DetectedMode.Unknown"/>. Tests run most-confident first,
    /// the same discipline as <see cref="SignalScribe.Analysis.DiscardClassifier"/>.
    /// </summary>
    public DetectedMode Classify()
    {
        if (Seconds < MinSeconds)
        {
            LastScore = default;
            return DetectedMode.Unknown;
        }

        var score = AnalyseHistogram();
        LastScore = score;

        // Asymmetric "levels" are not a modulation plan — every FSK scheme places its levels evenly
        // about the carrier, so a lopsided pair is some artefact of the audio instead.
        if (score.Symmetry < 0.8)
        {
            return DetectedMode.AnalogFm;
        }

        return score.Levels switch
        {
            4 => FourLevelMode(score.OuterDeviationHz, score.InnerDeviationHz),
            2 => TwoLevelMode(score.OuterDeviationHz),
            _ => DetectedMode.AnalogFm,
        };
    }

    /// <summary>
    /// Four discrete levels means C4FM — and deliberately stops there, without naming which C4FM.
    ///
    /// The temptation is to read the outer deviation and call ±1944 Hz DMR, separating it from the
    /// ±1800 Hz shared by P25, YSF and NXDN96. Measured end to end, that does not hold up. The two
    /// plans are only 8% apart, and three effects each move the measurement by a comparable amount:
    /// <list type="bullet">
    /// <item>The channel filter compresses the outer symbols — 4% measured with the carrier on a bin
    /// centre, 6% at 2.5 kHz off it, because the 12.5 kHz analysis grid does not align with the 5 kHz
    /// channel plan and the outer sidebands clip against the filter skirt.</item>
    /// <item>Transmitter deviation error of a few percent is ordinary and in either direction.</item>
    /// </list>
    /// Fold those together and DMR's plausible range overlaps P25's outright. Whatever threshold were
    /// chosen would be reporting the carrier's alignment as much as its modulation, and a wrong
    /// talkgroup on a confidently-named mode is worse than an honest "digital".
    ///
    /// The separation that does work is the sync pattern, which each mode defines distinctly and
    /// which no amount of filtering shifts. That belongs to the framers.
    /// </summary>
    private static DetectedMode FourLevelMode(double outer, double inner)
    {
        // Real C4FM puts the inner symbols at a third of the outer. Without that, four humps are some
        // other structure that happens to have four humps — worth not calling digital at all.
        var ratio = inner > 1 ? outer / inner : 0;
        return ratio is >= 2.2 and <= 4.2 ? DetectedMode.DigitalUnknown : DetectedMode.AnalogFm;
    }

    /// <summary>
    /// Two levels is 2FSK or GMSK, told apart by how wide the shift is: D-STAR runs ±1.2 kHz, POCSAG
    /// ±4.5 kHz. Anything between is left unnamed — 9600 baud packet lives there, and so does an AFSK
    /// burst, whose sine peaks read as a level pair until the soft-TNC decodes a frame.
    /// </summary>
    private static DetectedMode TwoLevelMode(double outer) => outer switch
    {
        > 950 and < 1_600 => DetectedMode.DStar,
        > 3_800 and < 5_200 => DetectedMode.Pocsag,
        _ => DetectedMode.DigitalUnknown,
    };

    /// <summary>Finds the symmetric symbol levels in the deviation histogram and how convincing they are.</summary>
    private ModeScore AnalyseHistogram()
    {
        // Two smoothings, for two different jobs. Detection wants the heavier one, so a level split
        // across bins is not counted twice. Locating the level wants the lighter one: every pass of
        // smoothing drags a peak down the ramp of transition samples it sits on, and 100 Hz of drag is
        // the whole difference between DMR's plan and P25's.
        Span<double> smooth = stackalloc double[HistBins];
        Span<double> light = stackalloc double[HistBins];
        Smooth(_hist, light, passes: 1);
        Smooth(_hist, smooth, passes: 2);

        double max = 0;
        foreach (var v in smooth)
        {
            max = Math.Max(max, v);
        }

        if (max <= 0)
        {
            return default;
        }

        Span<int> peaks = stackalloc int[16];
        var count = FindPeaks(smooth, max, peaks);
        count = MergeUnprominentPeaks(smooth, peaks, count, out var prominence);

        if (count is not (2 or 4))
        {
            return new ModeScore(count, 0, 0, prominence, 0);
        }

        Span<double> centres = stackalloc double[4];
        for (var i = 0; i < count; i++)
        {
            centres[i] = RefinePeak(light, peaks[i]);
        }

        // Levels come out of the scan in ascending deviation, so the pairs are ends-inward.
        var outer = (Math.Abs(centres[0]) + Math.Abs(centres[count - 1])) / 2;
        var inner = count == 4 ? (Math.Abs(centres[1]) + Math.Abs(centres[2])) / 2 : 0;

        // Symmetry: how well the negative side mirrors the positive one. Scored on the outer pair,
        // which has the longest lever arm and so is the most sensitive to a lopsided level plan.
        var lo = Math.Abs(centres[0]);
        var hi = Math.Abs(centres[count - 1]);
        var symmetry = Math.Min(lo, hi) / Math.Max(1e-9, Math.Max(lo, hi));

        return new ModeScore(count, outer, inner, prominence, symmetry);
    }

    /// <summary>
    /// Repeated 3-bin moving average. A symbol level lands across two or three bins once transmitter
    /// deviation error and receiver noise are in play, and an unsmoothed histogram splits one level
    /// into rival crests; much more than two passes starts merging ±648 into ±1944.
    /// </summary>
    private static void Smooth(long[] source, Span<double> destination, int passes)
    {
        for (var i = 0; i < HistBins; i++)
        {
            destination[i] = source[i];
        }

        Span<double> scratch = stackalloc double[HistBins];
        for (var p = 0; p < passes; p++)
        {
            destination.CopyTo(scratch);
            for (var i = 0; i < HistBins; i++)
            {
                destination[i] = (scratch[Math.Max(0, i - 1)] + scratch[i] + scratch[Math.Min(HistBins - 1, i + 1)]) / 3.0;
            }
        }
    }

    /// <summary>Local maxima that dominate a ±3-bin window and clear <see cref="MinPeakShare"/> of the busiest bin.</summary>
    private static int FindPeaks(ReadOnlySpan<double> smooth, double max, Span<int> peaks)
    {
        const int Window = 3;
        var count = 0;
        for (var i = 0; i < HistBins && count < peaks.Length; i++)
        {
            if (smooth[i] < max * MinPeakShare)
            {
                continue;
            }

            var dominates = true;
            for (var k = Math.Max(0, i - Window); k <= Math.Min(HistBins - 1, i + Window); k++)
            {
                // Strictly greater to the left, greater-or-equal to the right: on a flat crest this
                // accepts exactly the first bin rather than every bin of the plateau.
                if ((k < i && smooth[k] >= smooth[i]) || (k > i && smooth[k] > smooth[i]))
                {
                    dominates = false;
                    break;
                }
            }

            if (dominates)
            {
                peaks[count++] = i;
            }
        }

        return count;
    }

    /// <summary>
    /// Collapses peaks that are not separated by a real valley. This is what stops ripple on a single
    /// speech or noise hump from being counted as a symbol plan: between two genuine levels the signal
    /// only passes in transit, so the histogram there falls away, while ripple barely dips.
    /// </summary>
    private static int MergeUnprominentPeaks(ReadOnlySpan<double> smooth, Span<int> peaks, int count, out double prominence)
    {
        prominence = 1;
        var merged = true;
        while (merged && count > 1)
        {
            merged = false;
            for (var i = 0; i + 1 < count; i++)
            {
                var valley = double.MaxValue;
                for (var k = peaks[i]; k <= peaks[i + 1]; k++)
                {
                    valley = Math.Min(valley, smooth[k]);
                }

                var smaller = Math.Min(smooth[peaks[i]], smooth[peaks[i + 1]]);
                if (valley <= smaller * MaxValleyShare)
                {
                    continue;
                }

                // Too shallow to be two levels — keep the taller and drop the other.
                var drop = smooth[peaks[i]] >= smooth[peaks[i + 1]] ? i + 1 : i;
                for (var k = drop; k + 1 < count; k++)
                {
                    peaks[k] = peaks[k + 1];
                }

                count--;
                merged = true;
                break;
            }
        }

        // Report the shallowest surviving valley as the confidence in the level structure.
        for (var i = 0; i + 1 < count; i++)
        {
            var valley = double.MaxValue;
            for (var k = peaks[i]; k <= peaks[i + 1]; k++)
            {
                valley = Math.Min(valley, smooth[k]);
            }

            var smaller = Math.Min(smooth[peaks[i]], smooth[peaks[i + 1]]);
            prominence = Math.Min(prominence, 1 - (valley / Math.Max(1e-9, smaller)));
        }

        return count;
    }

    /// <summary>
    /// Sub-bin peak location by parabolic interpolation through the crest and its two neighbours.
    ///
    /// A centroid over the peak's shoulders would be the obvious choice and is badly wrong here: the
    /// samples caught in transit between symbols pile up on the *inner* side of every level, so a
    /// centroid is dragged toward zero — measured at ~100 Hz on synthetic DMR, which is most of the
    /// way from DMR's plan to P25's. Three-point interpolation reads the crest itself and barely
    /// notices the shoulder.
    /// </summary>
    private static double RefinePeak(ReadOnlySpan<double> hist, int peak)
    {
        if (peak <= 0 || peak >= HistBins - 1)
        {
            return BinCentreHz(peak);
        }

        var left = hist[peak - 1];
        var mid = hist[peak];
        var right = hist[peak + 1];
        var curvature = left - (2 * mid) + right;
        if (curvature >= 0)
        {
            return BinCentreHz(peak); // not a crest in the interpolated sense — trust the bin
        }

        var offset = 0.5 * (left - right) / curvature;
        return BinCentreHz(peak) + (Math.Clamp(offset, -1, 1) * BinWidthHz);
    }

    private static double BinCentreHz(int bin) => (bin * BinWidthHz) - HistRangeHz + (BinWidthHz / 2);
}
