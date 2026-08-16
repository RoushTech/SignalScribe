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
    public string Reason { get; set; } = string.Empty;

    public int VoicedMs { get; set; }

    public double SpeechBandRatio { get; set; }

    public double ModulationDepth { get; set; }

    public double SyllableRateHz { get; set; }

    public bool SustainedTone { get; set; }

    public void Configure(EntityTypeBuilder<DiscardedClip> builder)
    {
        builder.HasIndex(d => d.StartUtc);
        builder.Property(d => d.AudioPath).HasMaxLength(512);
        builder.Property(d => d.Reason).HasMaxLength(128);
    }
}
