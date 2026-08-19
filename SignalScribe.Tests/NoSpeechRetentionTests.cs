using SignalScribe.Analysis;
using SignalScribe.Enums;
using Xunit;

namespace SignalScribe.Tests;

/// <summary>
/// Which kept recordings have settled as empty and may age out. The two failures this guards:
/// hoarding squelch-tail hiss forever, and deleting audio the pipeline has not finished judging —
/// digital voice waiting on a vocoder above all.
/// </summary>
public class NoSpeechRetentionTests
{
    [Fact]
    public void ATranscribedEmptyClipIsPurgeable()
    {
        // The worker's VAD ran and found nothing — the authoritative "nothing was said".
        Assert.True(NoSpeechRetention.IsPurgeable(
            DetectedMode.AnalogFm, transcribed: true, voicedMs: 2_000, anySegmentHasText: false));
    }

    [Fact]
    public void ABarelyVoicedClipNeverQueuedIsPurgeable()
    {
        Assert.True(NoSpeechRetention.IsPurgeable(
            DetectedMode.AnalogFm, transcribed: false, voicedMs: 120, anySegmentHasText: false));
    }

    [Fact]
    public void AClipWithATranscriptIsKept()
    {
        Assert.False(NoSpeechRetention.IsPurgeable(
            DetectedMode.AnalogFm, transcribed: true, voicedMs: 2_000, anySegmentHasText: true));
    }

    [Fact]
    public void AVoicedClipStillAwaitingTranscriptionIsKept()
    {
        // Not yet judged — the purge must never race the transcription queue.
        Assert.False(NoSpeechRetention.IsPurgeable(
            DetectedMode.AnalogFm, transcribed: false, voicedMs: 2_000, anySegmentHasText: false));
    }

    [Theory]
    [InlineData(DetectedMode.DStar)]
    [InlineData(DetectedMode.Dmr)]
    public void DigitalVoiceNeverAgesOut(DetectedMode mode)
    {
        // The speech in there is real and waiting on a vocoder; deleting it forfeits
        // reprocess-when-models-improve.
        Assert.False(NoSpeechRetention.IsPurgeable(mode, transcribed: false, voicedMs: 100, anySegmentHasText: false));
    }

    [Theory]
    [InlineData(DetectedMode.Afsk1200)]
    [InlineData(DetectedMode.Pocsag)]
    public void DataClipsNeverAgeOut(DetectedMode mode)
    {
        Assert.False(NoSpeechRetention.IsPurgeable(mode, transcribed: false, voicedMs: 100, anySegmentHasText: false));
    }

    [Fact]
    public void ADoubleIsKeptWithoutASpecialCase()
    {
        // Never transcribed (doubles are excluded from the queue) and genuinely voiced: it fails
        // the settled-as-empty test on its own.
        Assert.False(NoSpeechRetention.IsPurgeable(
            DetectedMode.AnalogFm, transcribed: false, voicedMs: 1_500, anySegmentHasText: false));
    }
}
