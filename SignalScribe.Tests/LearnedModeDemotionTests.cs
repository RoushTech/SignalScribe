using SignalScribe.Analysis;
using SignalScribe.Enums;
using Xunit;

namespace SignalScribe.Tests;

/// <summary>
/// Undoing a falsely learned digital-voice mode. The failure this guards: a mode label that
/// silences transcription and exempts the channel from the voice audit, on a channel that was
/// analog FM all along — and that analog traffic can never overwrite, because analog is not an
/// identified mode.
/// </summary>
public class LearnedModeDemotionTests
{
    [Theory]
    [InlineData(DetectedMode.DStar)]
    [InlineData(DetectedMode.Dmr)]
    [InlineData(DetectedMode.P25Phase1)]
    public void ATranscriptDisprovesALearnedDigitalVoiceMode(DetectedMode learned)
    {
        Assert.True(LearnedModeDemotion.TranscriptDisproves(learned));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(DetectedMode.Unknown)]
    [InlineData(DetectedMode.AnalogFm)]
    [InlineData(DetectedMode.DigitalUnknown)]
    [InlineData(DetectedMode.Afsk1200)] // data never suppressed transcription — nothing to undo
    [InlineData(DetectedMode.Pocsag)]
    public void OnlyDigitalVoiceIsDisprovableByATranscript(DetectedMode? learned)
    {
        Assert.False(LearnedModeDemotion.TranscriptDisproves(learned));
    }

    [Fact]
    public void AHeaderlessUntranscribedDigitalVoiceRecordingIsASuspect()
    {
        Assert.True(LearnedModeDemotion.IsSuspect(
            DetectedMode.DStar, hasDecodedHeader: false, alreadyTranscribed: false, voicedMs: 5_000, isDouble: false));
    }

    [Fact]
    public void ADecodedHeaderVouchesForTheModeAndBlocksRequeue()
    {
        // The CRC proved that one; no transcript elsewhere on the channel can unsay it.
        Assert.False(LearnedModeDemotion.IsSuspect(
            DetectedMode.DStar, hasDecodedHeader: true, alreadyTranscribed: false, voicedMs: 5_000, isDouble: false));
    }

    [Fact]
    public void RequeueMirrorsTheIngestGate()
    {
        // Already transcribed, not voice-like, or a heterodyne double: none of these would have
        // been queued had the mode read analog, so none may be queued now.
        Assert.False(LearnedModeDemotion.IsSuspect(
            DetectedMode.DStar, hasDecodedHeader: false, alreadyTranscribed: true, voicedMs: 5_000, isDouble: false));
        Assert.False(LearnedModeDemotion.IsSuspect(
            DetectedMode.DStar, hasDecodedHeader: false, alreadyTranscribed: false, voicedMs: 200, isDouble: false));
        Assert.False(LearnedModeDemotion.IsSuspect(
            DetectedMode.DStar, hasDecodedHeader: false, alreadyTranscribed: false, voicedMs: 5_000, isDouble: true));
    }

    [Fact]
    public void NonDigitalVoiceModesAreNeverSuspects()
    {
        Assert.False(LearnedModeDemotion.IsSuspect(
            DetectedMode.AnalogFm, hasDecodedHeader: false, alreadyTranscribed: false, voicedMs: 5_000, isDouble: false));
        Assert.False(LearnedModeDemotion.IsSuspect(
            DetectedMode.Afsk1200, hasDecodedHeader: false, alreadyTranscribed: false, voicedMs: 5_000, isDouble: false));
    }

    [Fact]
    public void DigitalVoiceModesCoverExactlyTheVoiceCodedModes()
    {
        Assert.All(LearnedModeDemotion.DigitalVoiceModes, m => Assert.True(m.IsDigitalVoice()));
        Assert.Contains(DetectedMode.DStar, LearnedModeDemotion.DigitalVoiceModes);
        Assert.DoesNotContain(DetectedMode.Pocsag, LearnedModeDemotion.DigitalVoiceModes);
    }
}
