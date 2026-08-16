using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SignalScribe.Data.Models;

/// <summary>A clustered stretch of activity on one channel (a QSO or a net occurrence).</summary>
public class Session : IEntityTypeConfiguration<Session>
{
    public long Id { get; set; }

    public int ChannelId { get; set; }

    public Channel Channel { get; set; } = null!;

    public DateTime StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    public bool IsNet { get; set; }

    public long? NetId { get; set; }

    public Net? Net { get; set; }

    /// <summary>Narrative summary written by the local LLM. Facts (roster, NCS, duration) are always derived from the database, never from this text.</summary>
    public string? Summary { get; set; }

    public string? SummaryModel { get; set; }

    public ICollection<Transmission> Transmissions { get; set; } = [];

    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.HasIndex(s => new { s.ChannelId, s.StartUtc });
        builder.Property(s => s.SummaryModel).HasMaxLength(128);

        // Deleting a net keeps its session history; occurrences just unlink.
        builder.HasOne(s => s.Net)
            .WithMany(n => n.Sessions)
            .HasForeignKey(s => s.NetId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
