using SignalScribe.Analysis;
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
}
