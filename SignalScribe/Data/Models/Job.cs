using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalScribe.Enums;

namespace SignalScribe.Data.Models;

/// <summary>Work-queue row. Workers claim with a lease via the host API; completions are idempotent.</summary>
public class Job : IEntityTypeConfiguration<Job>
{
    public long Id { get; set; }

    public JobType Type { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Pending;

    public string PayloadJson { get; set; } = "{}";

    public int Attempts { get; set; }

    public string? LeasedBy { get; set; }

    public DateTime? LeaseUntilUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }

    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.HasIndex(j => new { j.Status, j.Type });
        builder.Property(j => j.LeasedBy).HasMaxLength(128);
    }
}
