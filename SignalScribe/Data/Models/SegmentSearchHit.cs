using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SignalScribe.Data.Models;

/// <summary>
/// Keyless projection for FTS5 transcript search. Never materialized as a table — populated exclusively via
/// <c>FromSql</c> against the <c>segment_fts</c> virtual table (created in a raw-SQL migration).
/// </summary>
public class SegmentSearchHit : IEntityTypeConfiguration<SegmentSearchHit>
{
    public long SegmentId { get; set; }

    public string Snippet { get; set; } = string.Empty;

    public double Rank { get; set; }

    public void Configure(EntityTypeBuilder<SegmentSearchHit> builder)
    {
        builder.HasNoKey();
        builder.ToView(null);
    }
}
