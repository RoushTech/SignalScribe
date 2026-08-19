using SignalScribe.Analysis;
using SignalScribe.Enums;
using Xunit;

namespace SignalScribe.Tests;

public class ChannelVoiceAuditTests
{
    [Fact]
    public void DisablesADataFrequencyThatNeverCarriesSpeech()
    {
        // 144.390 APRS: 1072 recordings, not one word in any of them.
        var reason = ChannelVoiceAudit.DisableReason(resolvedCount: 1072, speechCount: 0, lastSpeechUtc: null);
        Assert.NotNull(reason);
        Assert.Contains("no speech", reason);
    }

    [Fact]
    public void KeepsAChannelThatHasEverProducedSpeech()
    {
        Assert.Null(ChannelVoiceAudit.DisableReason(500, speechCount: 1, lastSpeechUtc: null));
        Assert.Null(ChannelVoiceAudit.DisableReason(500, speechCount: 0, lastSpeechUtc: DateTime.UtcNow.AddDays(-30)));
    }

    [Fact]
    public void WaitsForEnoughEvidenceBeforeJudging()
    {
        // A brand-new repeater channel that has only been heard a few times keeps its bypass.
        Assert.Null(ChannelVoiceAudit.DisableReason(ChannelVoiceAudit.MinResolvedRecordings - 1, 0, null));
        Assert.NotNull(ChannelVoiceAudit.DisableReason(ChannelVoiceAudit.MinResolvedRecordings, 0, null));
    }

    [Fact]
    public void ABacklogOfUntranscribedClipsCannotDisableALiveChannel()
    {
        // Only settled recordings are counted, so 500 clips waiting in the queue read as zero
        // evidence — a slow worker must never cost a busy repeater its known-channel status.
        Assert.Null(ChannelVoiceAudit.DisableReason(resolvedCount: 0, speechCount: 0, lastSpeechUtc: null));
    }

    [Theory]
    [InlineData(DetectedMode.Dmr)]
    [InlineData(DetectedMode.DStar)]
    [InlineData(DetectedMode.Pocsag)]
    public void KeepsAChannelWhoseModeWeCanName(DetectedMode mode)
    {
        // A digital voice channel produces no transcript today only because its vocoder is not wired
        // up yet. Disabling it would drop it from the known set and stop us decoding the very thing
        // we just identified — the silence is a gap in us, not evidence about the frequency.
        Assert.Null(ChannelVoiceAudit.DisableReason(1072, speechCount: 0, lastSpeechUtc: null, learnedMode: mode));
    }

    [Theory]
    [InlineData(DetectedMode.DigitalUnknown)]
    [InlineData(DetectedMode.AnalogFm)]
    [InlineData(DetectedMode.Unknown)]
    [InlineData(null)]
    public void StillDisablesAChannelWeCannotName(DetectedMode? mode)
    {
        // Knowing a frequency carries *something* digital is not knowing what it is, and it must not
        // buy an exemption — that is how 144.390 recorded 1072 packets in the first place.
        Assert.NotNull(ChannelVoiceAudit.DisableReason(1072, speechCount: 0, lastSpeechUtc: null, learnedMode: mode));
    }
}
