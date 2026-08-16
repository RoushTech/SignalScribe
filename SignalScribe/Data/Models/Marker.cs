using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalScribe.Enums;

namespace SignalScribe.Data.Models;

/// <summary>Segmentation boundary evidence inside a transmission. Squelch gates recording; markers define boundaries.</summary>
public class Marker : IEntityTypeConfiguration<Marker>
{
    public long Id { get; set; }

    public long TransmissionId { get; set; }

    public Transmission Transmission { get; set; } = null!;

    public MarkerType Type { get; set; }

    /// <summary>Offset from clip start, milliseconds.</summary>
    public int OffsetMs { get; set; }

    /// <summary>0..1 detector confidence.</summary>
    public double Confidence { get; set; }

    public void Configure(EntityTypeBuilder<Marker> builder)
    {
        builder.HasIndex(m => new { m.TransmissionId, m.OffsetMs });
    }
}
