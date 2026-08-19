using SignalScribe.Analysis;
using Xunit;

namespace SignalScribe.Tests;

public class SpeechPackerTests
{
    [Fact]
    public void ShortSpansShareOneWindow()
    {
        // Six 2-second overs: 12 s of audio plus 1.5 s of padding, comfortably inside one window.
        var windows = SpeechPacker.Plan([2000, 2000, 2000, 2000, 2000, 2000]);

        Assert.Single(windows);
        Assert.Equal(6, windows[0].Placements.Count);
        Assert.Equal([0, 1, 2, 3, 4, 5], windows[0].Placements.Select(p => p.SpanIndex));
    }

    [Fact]
    public void PaddingSeparatesConsecutiveSpans()
    {
        var w = SpeechPacker.Plan([1000, 1000])[0];

        Assert.Equal(0, w.Placements[0].OffsetMs);
        Assert.Equal(1000 + SpeechPacker.PadMs, w.Placements[1].OffsetMs);
        Assert.Equal(2000 + SpeechPacker.PadMs, w.TotalMs);
    }

    /// <summary>Whisper truncates at its window, so nothing may be planned past the usable length.</summary>
    [Fact]
    public void NoWindowExceedsTheUsableLength()
    {
        var windows = SpeechPacker.Plan([.. Enumerable.Repeat(4000, 20)]);

        Assert.True(windows.Count > 1);
        Assert.All(windows, w => Assert.True(w.TotalMs <= SpeechPacker.MaxWindowMs, $"window was {w.TotalMs} ms"));
        Assert.Equal(20, windows.Sum(w => w.Placements.Count));
    }

    /// <summary>Every span must be planned exactly once, or an over silently loses its transcript.</summary>
    [Fact]
    public void EverySpanIsPlacedExactlyOnce()
    {
        var lengths = new[] { 500, 30_000, 1200, 800, 26_000, 900 };
        var placed = SpeechPacker.Plan(lengths).SelectMany(w => w.Placements).Select(p => p.SpanIndex).ToList();

        Assert.Equal(lengths.Length, placed.Count);
        Assert.Equal(Enumerable.Range(0, lengths.Length), placed.OrderBy(i => i));
    }

    /// <summary>A span too big for a window is handed over whole — Whisper splits it internally.</summary>
    [Fact]
    public void AnOversizedSpanGetsItsOwnWindow()
    {
        var windows = SpeechPacker.Plan([1000, 45_000, 1000]);

        var solo = Assert.Single(windows, w => w.Placements.Count == 1 && w.Placements[0].SpanIndex == 1);
        Assert.Equal(45_000, solo.TotalMs);
    }

    [Fact]
    public void TextIsAttributedToTheSpanItLandsIn()
    {
        var w = SpeechPacker.Plan([2000, 2000, 2000])[0];

        Assert.Equal(0, SpeechPacker.SpanAt(w, 1000));
        Assert.Equal(1, SpeechPacker.SpanAt(w, 2000 + SpeechPacker.PadMs + 1000));
        Assert.Equal(2, SpeechPacker.SpanAt(w, (2 * (2000 + SpeechPacker.PadMs)) + 1000));
    }

    /// <summary>
    /// Whisper's boundaries drift, so text can be timestamped inside the padding. Attaching it to
    /// the nearest over beats dropping the words entirely.
    /// </summary>
    [Fact]
    public void TextInThePaddingGoesToTheNearestSpan()
    {
        var w = SpeechPacker.Plan([2000, 2000])[0];

        Assert.Equal(0, SpeechPacker.SpanAt(w, 2050));                        // just after the first span
        Assert.Equal(1, SpeechPacker.SpanAt(w, 2000 + SpeechPacker.PadMs - 50)); // just before the second
        Assert.Equal(1, SpeechPacker.SpanAt(w, 999_999));                     // past the end
    }

    [Fact]
    public void NoSpansPlansNoWork()
    {
        Assert.Empty(SpeechPacker.Plan([]));
    }
}
