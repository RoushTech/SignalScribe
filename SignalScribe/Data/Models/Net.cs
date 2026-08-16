using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalScribe.Enums;

namespace SignalScribe.Data.Models;

/// <summary>
/// A recurring net on a channel — declared manually or discovered by recurrence mining
/// (<see cref="Source"/>). Each occurrence is a <see cref="Session"/> with <c>NetId</c> set;
/// a session overlapping the declared window classifies deterministically (no heuristics).
/// Schedule is stored in UTC like every timestamp — the browser converts for display/entry.
/// </summary>
public class Net : IEntityTypeConfiguration<Net>
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public int ChannelId { get; set; }

    public Channel Channel { get; set; } = null!;

    public NetScheduleSource Source { get; set; } = NetScheduleSource.Manual;

    public DayOfWeek? DayOfWeekUtc { get; set; }

    public TimeOnly? StartTimeUtc { get; set; }

    public int? DurationMinutes { get; set; }

    public ICollection<Session> Sessions { get; set; } = [];

    public void Configure(EntityTypeBuilder<Net> builder)
    {
        builder.HasIndex(n => n.ChannelId);
        builder.Property(n => n.Name).HasMaxLength(128);
        builder.Property(n => n.Description).HasMaxLength(512);
    }
}
