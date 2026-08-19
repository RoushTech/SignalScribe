using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SignalScribe.Data.Models;

/// <summary>
/// A recording the capture gate rejected (not voice, or too short). Retained briefly with the
/// measurements behind the decision so the operator can listen and judge whether the gate was
/// right — then purged on a schedule. Has no Channel relationship on purpose: reviewing a discard
/// must never bring a channel into existence.
/// </summary>
public class DiscardedClip : IEntityTypeConfiguration<DiscardedClip>
{
    public long Id { get; set; }

    public long FrequencyHz { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public string AudioPath { get; set; } = string.Empty;

    public double PeakDbfs { get; set; }

    /// <summary>Why it was rejected, e.g. "not voice" or "only 120 ms of signal".</summary>
    public SignalScribe.Enums.DiscardReason Reason { get; set; }

    public int VoicedMs { get; set; }

    public double SpeechBandRatio { get; set; }

    public double ModulationDepth { get; set; }

    public double SyllableRateHz { get; set; }

    public bool SustainedTone { get; set; }

    /// <summary>CTCSS tone measured under the clip, in Hz — tells you whose system a rejected signal belonged to.</summary>
    public double? CtcssHz { get; set; }

    /// <summary>DCS code decoded from the clip, as the octal number operators quote.</summary>
    public int? DcsCode { get; set; }

    /// <summary>Modulation measured from the discriminator. Turns "not speech" into what it actually was.</summary>
    public SignalScribe.Enums.DetectedMode Mode { get; set; }

    public void Configure(EntityTypeBuilder<DiscardedClip> builder)
    {
        builder.HasIndex(d => d.StartUtc);
        builder.Property(d => d.AudioPath).HasMaxLength(512);
    }
}
