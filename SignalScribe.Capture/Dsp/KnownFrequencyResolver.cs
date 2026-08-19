namespace SignalScribe.Capture.Dsp;

/// <summary>
/// Maps a frequency the bank is looking at onto the known channel it serves, if any.
///
/// The known set holds *real* channel frequencies (147.180), while the bank works in analysis-grid
/// terms — a bin centre before the carrier offset settles (147.175), a snapped measurement after.
/// An exact-match lookup therefore silently fails for every known channel that sits off the 12.5 kHz
/// grid, which on a 5 kHz band plan is most of them: measured on air, 147.180 and 144.920 (both
/// 5 kHz off their bins) had the voice-likeness gate applied to every transmission — and real overs
/// discarded — despite being known, enabled channels, because `known` was looked up with the bin.
///
/// Resolution is by distance instead: the known channel within half a bin of the given frequency is
/// the one this gate is recording. Half a bin is exactly the region whose energy lands in the bin,
/// so a match is physical rather than heuristic; if two known channels share a bin the nearer one
/// wins, which is the best available answer for a gate that can only be one transmission at a time.
/// </summary>
public static class KnownFrequencyResolver
{
    /// <summary>The known frequency within <paramref name="halfSpacingHz"/> of <paramref name="frequencyHz"/>, nearest first; null when none is.</summary>
    public static long? Nearest(IReadOnlyCollection<long> knownFrequencies, long frequencyHz, long halfSpacingHz)
    {
        long? best = null;
        var bestDistance = halfSpacingHz;
        foreach (var known in knownFrequencies)
        {
            var distance = Math.Abs(known - frequencyHz);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = known;
            }
        }

        return best;
    }
}
