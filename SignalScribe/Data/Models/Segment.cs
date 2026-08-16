using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SignalScribe.Data.Models;

/// <summary>A speaker-attributed span of a transmission, produced by segmentation. Carries the transcript and speaker embedding.</summary>
public class Segment : IEntityTypeConfiguration<Segment>
{
    public long Id { get; set; }

    public long TransmissionId { get; set; }

    public Transmission Transmission { get; set; } = null!;

    public int StartMs { get; set; }

    public int EndMs { get; set; }

    public string? Transcript { get; set; }

    /// <summary>Model name+version that produced the transcript, e.g. "whisper.cpp/small.en-q5_1". Audio is ground truth; results are reproducible.</summary>
    public string? TranscriptionModel { get; set; }

    public byte[]? SpeakerEmbedding { get; set; }

    public string? EmbeddingModel { get; set; }

    /// <summary>Callsign extracted from this segment's transcript, normalized (e.g. "KD9ABC").</summary>
    public string? Callsign { get; set; }

    public long? SpeakerId { get; set; }

    public Speaker? Speaker { get; set; }

    public void Configure(EntityTypeBuilder<Segment> builder)
    {
        builder.HasIndex(s => s.TransmissionId);
        builder.HasIndex(s => s.Callsign);
        builder.Property(s => s.TranscriptionModel).HasMaxLength(128);
        builder.Property(s => s.EmbeddingModel).HasMaxLength(128);
        builder.Property(s => s.Callsign).HasMaxLength(16);
    }
}
