using SignalScribe.Analysis;
using SignalScribe.Enums;
using Xunit;

namespace SignalScribe.Tests;

public class ClipSplitterTests
{
    /// <summary>
    /// Transmission 6 off the air: two overs in one 14.3 s recording. Whisper decoded the whole clip
    /// as one utterance and stopped after the first over; the markers say where the split belongs.
    /// </summary>
    [Fact]
    public void SplitsRealTwoOverRecordingAtTheUnkey()
    {
        var spans = ClipSplitter.Spans(
            [
                (MarkerType.RfEdgeRise, 0),
                (MarkerType.CourtesyTone, 3360),
                (MarkerType.DcOffsetJump, 3900),
                (MarkerType.CourtesyTone, 3904),
                (MarkerType.DcOffsetJump, 4000),
                (MarkerType.CourtesyTone, 4128),
                (MarkerType.CourtesyTone, 13984),
                (MarkerType.DcOffsetJump, 14000),
                (MarkerType.DcOffsetJump, 14100),
                (MarkerType.RfEdgeFall, 14336),
            ],
            durationMs: 14336);

        Assert.Equal([(0, 3360), (3360, 14336)], spans);
    }

    [Fact]
    public void CollapsesOneUnkeysWorthOfMarkersIntoASingleSplit()
    {
        // Courtesy tone, squelch crash and DC jump all fire for the same unkey.
        var spans = ClipSplitter.Spans(
            [
                (MarkerType.CourtesyTone, 5000),
                (MarkerType.SquelchCrash, 5120),
                (MarkerType.DcOffsetJump, 5300),
            ],
            durationMs: 10_000);

        Assert.Equal([(0, 5000), (5000, 10_000)], spans);
    }

    [Fact]
    public void IgnoresRfEdgesWhichBoundTheClipRatherThanDivideIt()
    {
        var spans = ClipSplitter.Spans(
            [(MarkerType.RfEdgeRise, 0), (MarkerType.RfEdgeFall, 8000)],
            durationMs: 8000);

        Assert.Equal([(0, 8000)], spans);
    }

    [Fact]
    public void DropsBoundariesThatWouldLeaveATooShortSpan()
    {
        // A tail marker 200 ms before the end is the closing unkey, not a second over.
        var spans = ClipSplitter.Spans(
            [(MarkerType.CourtesyTone, 200), (MarkerType.CourtesyTone, 7800)],
            durationMs: 8000);

        Assert.Equal([(0, 8000)], spans);
    }

    [Fact]
    public void SplitsQuickKeyingIntoEveryOver()
    {
        var spans = ClipSplitter.Spans(
            [
                (MarkerType.SquelchCrash, 4000),
                (MarkerType.SquelchCrash, 9000),
                (MarkerType.SquelchCrash, 15_000),
            ],
            durationMs: 20_000);

        Assert.Equal([(0, 4000), (4000, 9000), (9000, 15_000), (15_000, 20_000)], spans);
    }

    [Fact]
    public void EmbeddingSplitsFromDiarizationAlsoDivideSpans()
    {
        // One station keyed for the whole over, but the embedding pass heard two voices.
        var spans = ClipSplitter.Spans(
            [(MarkerType.EmbeddingSplit, 6000)],
            durationMs: 12_000);

        Assert.Equal([(0, 6000), (6000, 12_000)], spans);
    }
}
