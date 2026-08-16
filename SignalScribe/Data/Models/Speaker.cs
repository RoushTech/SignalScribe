using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SignalScribe.Data.Models;

/// <summary>A clustered voice across transmissions, labeled with a callsign when one is extracted from its segments.</summary>
public class Speaker : IEntityTypeConfiguration<Speaker>
{
    public long Id { get; set; }

    public string? Callsign { get; set; }

    public string? Label { get; set; }

    public byte[]? EmbeddingCentroid { get; set; }

    public string? EmbeddingModel { get; set; }

    public DateTime FirstHeardUtc { get; set; }

    public DateTime LastHeardUtc { get; set; }

    public ICollection<Segment> Segments { get; set; } = [];

    public void Configure(EntityTypeBuilder<Speaker> builder)
    {
        builder.HasIndex(s => s.Callsign);
        builder.Property(s => s.Callsign).HasMaxLength(16);
        builder.Property(s => s.Label).HasMaxLength(128);
        builder.Property(s => s.EmbeddingModel).HasMaxLength(128);
    }
}
