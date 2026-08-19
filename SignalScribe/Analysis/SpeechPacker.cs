namespace SignalScribe.Analysis;

/// <summary>
/// Packs several short spans into one Whisper run.
///
/// Whisper pads every run to a 30-second mel window, so the cost is per *run*, not per second —
/// measured on real air, a 1-second span and a 20-second span both took ~6.3 s, and a seven-span
/// clip took seven times that. Spans that fit inside one window together should therefore be
/// decoded together.
///
/// The reason this is not simply "hand Whisper the whole clip" — the failure <see cref="ClipSplitter"/>
/// exists to prevent — is the gap. A repeater's inter-over gap is a second or more of squelch tail,
/// and Whisper reads that as end-of-speech and stops, losing everything after it. Packing replaces
/// those gaps with a <see cref="PadMs"/> silence: long enough to keep the spans apart for
/// attribution, far too short to read as the end of the utterance.
/// </summary>
public static class SpeechPacker
{
    /// <summary>Silence inserted between packed spans.</summary>
    public const int PadMs = 300;

    /// <summary>
    /// Usable audio per window. Below Whisper's 30 s so the padding and any timestamp slop cannot
    /// push the last span over the edge, where it would be silently truncated.
    /// </summary>
    public const int MaxWindowMs = 27_000;

    /// <summary>A span's placement inside a packed window: where it lands, and how long it is.</summary>
    public readonly record struct Placement(int SpanIndex, int OffsetMs, int LengthMs)
    {
        public int EndMs => OffsetMs + LengthMs;
    }

    /// <summary>One Whisper run: the spans inside it and the total buffer length to allocate.</summary>
    public sealed record Window(IReadOnlyList<Placement> Placements, int TotalMs);

    /// <summary>
    /// Lays spans out into as few windows as possible, in order. Order is preserved rather than
    /// bin-packed by size: the spans are consecutive overs of one conversation, and Whisper's prompt
    /// context carries forward within a run, so shuffling them would degrade the transcript to save
    /// nothing measurable.
    /// </summary>
    /// <param name="spanLengthsMs">Length of each span, indexed as the caller's span list.</param>
    public static IReadOnlyList<Window> Plan(IReadOnlyList<int> spanLengthsMs)
    {
        var windows = new List<Window>();
        var current = new List<Placement>();
        var cursor = 0;

        foreach (var (length, index) in spanLengthsMs.Select((l, i) => (l, i)))
        {
            // A span longer than a window gets one to itself and is handed over whole — Whisper
            // splits it internally across as many windows as it needs, which is the correct
            // behaviour and not something to second-guess here.
            if (length >= MaxWindowMs)
            {
                Flush();
                windows.Add(new Window([new Placement(index, 0, length)], length));
                continue;
            }

            var offset = current.Count == 0 ? 0 : cursor + PadMs;
            if (offset + length > MaxWindowMs)
            {
                Flush();
                offset = 0;
            }

            current.Add(new Placement(index, offset, length));
            cursor = offset + length;
        }

        Flush();
        return windows;

        void Flush()
        {
            if (current.Count > 0)
            {
                windows.Add(new Window([.. current], cursor));
                current = [];
                cursor = 0;
            }
        }
    }

    /// <summary>
    /// Which span a piece of decoded text belongs to, given where Whisper timestamped it inside the
    /// packed window. Text landing in the padding is attributed to the nearest span rather than
    /// dropped: Whisper's boundaries drift by tens of milliseconds, and losing a word to a rounding
    /// error is worse than attaching it to the over it almost certainly came from.
    /// </summary>
    public static int SpanAt(Window window, int offsetMs)
    {
        var best = window.Placements[0].SpanIndex;
        var bestDistance = int.MaxValue;
        foreach (var p in window.Placements)
        {
            if (offsetMs >= p.OffsetMs && offsetMs <= p.EndMs)
            {
                return p.SpanIndex;
            }

            var distance = offsetMs < p.OffsetMs ? p.OffsetMs - offsetMs : offsetMs - p.EndMs;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = p.SpanIndex;
            }
        }

        return best;
    }
}
