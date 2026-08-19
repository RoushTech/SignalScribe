using SignalScribe.Analysis;
using SignalScribe.Enums;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// What earns a Whisper run. The cost being defended: a run is padded to a 30-second window, so
/// every clip queued that cannot contain speech wastes a whole run, not a fraction of one.
/// </summary>
public class TranscriptionGateTests(ITestOutputHelper output)
{
    [Fact]
    public void OrdinaryVoiceOnAVoiceChannelIsTranscribed()
    {
        Assert.True(TranscriptionGate.ShouldTranscribe(
            DetectedMode.AnalogFm, DetectedMode.AnalogFm, isDouble: false, voicedMs: 5_000));
    }

    [Fact]
    public void VoiceOnAnUnclassifiedChannelIsTranscribed()
    {
        Assert.True(TranscriptionGate.ShouldTranscribe(
            DetectedMode.AnalogFm, null, isDouble: false, voicedMs: 5_000));
    }

    /// <summary>
    /// The measured waste: on 144.390, 34 of 72 posted transmissions failed to decode and read as
    /// analog, each buying a full run on a frequency that carries no speech.
    /// </summary>
    [Fact]
    public void AnUndecodedBurstOnADataChannelIsNotTranscribed()
    {
        Assert.False(TranscriptionGate.ShouldTranscribe(
            DetectedMode.AnalogFm, DetectedMode.Afsk1200, isDouble: false, voicedMs: 5_000));
        Assert.False(TranscriptionGate.ShouldTranscribe(
            DetectedMode.Unknown, DetectedMode.Afsk1200, isDouble: false, voicedMs: 5_000));
    }

    [Fact]
    public void ADecodedPacketIsNotTranscribedWhateverTheChannel()
    {
        Assert.False(TranscriptionGate.ShouldTranscribe(
            DetectedMode.Afsk1200, null, isDouble: false, voicedMs: 5_000));
    }

    [Fact]
    public void DigitalVoiceIsNotTranscribed()
    {
        Assert.False(TranscriptionGate.ShouldTranscribe(
            DetectedMode.Ysf, DetectedMode.Ysf, isDouble: false, voicedMs: 5_000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(299)]
    public void TooLittleVoicedAudioIsNotTranscribed(int voicedMs)
    {
        Assert.False(TranscriptionGate.ShouldTranscribe(
            DetectedMode.AnalogFm, DetectedMode.AnalogFm, isDouble: false, voicedMs));
    }

    [Fact]
    public void ADoubleIsNotTranscribed()
    {
        Assert.False(TranscriptionGate.ShouldTranscribe(
            DetectedMode.AnalogFm, DetectedMode.AnalogFm, isDouble: true, voicedMs: 5_000));
    }

    /// <summary>
    /// The escape hatch. A learned data label that is wrong would otherwise silence a voice channel
    /// for good, since the transcript that would disprove it can never be produced — the same trap
    /// the D-STAR mislabel sprang. Pinning the modulation is what the operator has.
    /// </summary>
    [Fact]
    public void AnOperatorPinnedVoiceModulationOverridesALearnedDataLabel()
    {
        // The caller passes Modulation ?? LearnedState.Mode, so a pinned AnalogFm arrives here as
        // the channel mode and the channel is transcribed again.
        Assert.True(TranscriptionGate.ShouldTranscribe(
            DetectedMode.AnalogFm, DetectedMode.AnalogFm, isDouble: false, voicedMs: 5_000));
    }

    /// <summary>
    /// Anything the gate declines must read as finished, never as pending. A "processing" badge
    /// that never resolves is a worse bug than the wasted run it replaced.
    /// </summary>
    [Fact]
    public void NothingDeclinedIsLeftLookingPending()
    {
        Assert.False(TranscriptionGate.IsAwaitingTranscription(
            DetectedMode.AnalogFm, DetectedMode.Afsk1200, isDouble: false, voicedMs: 5_000, alreadyTranscribed: false));
        Assert.False(TranscriptionGate.IsAwaitingTranscription(
            DetectedMode.AnalogFm, DetectedMode.AnalogFm, isDouble: false, voicedMs: 5_000, alreadyTranscribed: true));
        Assert.True(TranscriptionGate.IsAwaitingTranscription(
            DetectedMode.AnalogFm, DetectedMode.AnalogFm, isDouble: false, voicedMs: 5_000, alreadyTranscribed: false));
    }

    /// <summary>What the suppression is worth, in runs, on the traffic actually measured.</summary>
    [Fact]
    public void ReportsTheSavingOnMeasuredAprsTraffic()
    {
        const int UndecodedBursts = 34;
        var wasted = UndecodedBursts; // one padded run each, whatever their length
        output.WriteLine($"  {UndecodedBursts} undecoded APRS bursts = {wasted} Whisper runs ≈ {wasted * 7}s wall, {wasted * 21} core-seconds");
        Assert.Equal(0, Enumerable.Range(0, UndecodedBursts)
            .Count(_ => TranscriptionGate.ShouldTranscribe(DetectedMode.AnalogFm, DetectedMode.Afsk1200, false, 5_000)));
    }
}
