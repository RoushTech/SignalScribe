using SignalScribe.Enums;

namespace SignalScribe.Analysis;

/// <summary>
/// Splits a recording into per-over spans using the capture-side segmentation markers.
///
/// Whisper decodes a clip as one utterance and will stop at the first end-of-speech it believes in
/// — on a repeater that is the squelch crash between overs, so everything after the first over is
/// silently lost. Capture already marks those boundaries (courtesy tones, squelch crashes,
/// discriminator DC jumps); transcribing each span separately is what makes them useful.
/// </summary>
public static class ClipSplitter
{
    /// <summary>Markers that indicate one station stopped and another (or the same) started.</summary>
    private static bool IsBoundary(MarkerType type) => type is
        MarkerType.CourtesyTone or MarkerType.SquelchCrash or MarkerType.DcOffsetJump or MarkerType.EmbeddingSplit;

    /// <summary>
    /// Returns ordered [start, end) spans in milliseconds. Boundary markers within
    /// <paramref name="clusterMs"/> collapse into one split (a single unkey fires several
    /// detectors), and spans shorter than <paramref name="minSpanMs"/> are merged away.
    /// </summary>
    public static IReadOnlyList<(int StartMs, int EndMs)> Spans(
        IEnumerable<(MarkerType Type, int OffsetMs)> markers,
        int durationMs,
        int minSpanMs = 700,
        int clusterMs = 700)
    {
        var boundaries = new List<int>();
        int? previousMs = null;
        foreach (var m in markers.Where(m => IsBoundary(m.Type)).OrderBy(m => m.OffsetMs))
        {
            if (m.OffsetMs <= 0 || m.OffsetMs >= durationMs)
            {
                continue;
            }

            // Chain against the previous marker, not the start of the cluster: one unkey fires the
            // tone, crash and DC detectors in sequence and the chain can outrun a fixed window.
            var sameUnkey = previousMs is { } prev && m.OffsetMs - prev <= clusterMs;
            previousMs = m.OffsetMs;
            if (!sameUnkey)
            {
                boundaries.Add(m.OffsetMs);
            }
        }

        var spans = new List<(int StartMs, int EndMs)>();
        var start = 0;
        foreach (var b in boundaries)
        {
            if (b - start >= minSpanMs && durationMs - b >= minSpanMs)
            {
                spans.Add((start, b));
                start = b;
            }
        }

        spans.Add((start, durationMs));
        return spans;
    }
}
