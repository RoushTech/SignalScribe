using Microsoft.EntityFrameworkCore;
using SignalScribe.Data.Models;

namespace SignalScribe.Data;

public class SignalScribeContext(DbContextOptions<SignalScribeContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<Transmission> Transmissions => Set<Transmission>();

    public DbSet<Marker> Markers => Set<Marker>();

    public DbSet<Segment> Segments => Set<Segment>();

    public DbSet<Speaker> Speakers => Set<Speaker>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Net> Nets => Set<Net>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<DiscardedClip> DiscardedClips => Set<DiscardedClip>();

    public DbSet<CaptureSettings> CaptureSettings => Set<CaptureSettings>();

    public DbSet<WorkerSettings> WorkerSettings => Set<WorkerSettings>();

    /// <summary>FTS5 search projection — query via <c>FromSql</c> only.</summary>
    public DbSet<SegmentSearchHit> SegmentSearchHits => Set<SegmentSearchHit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SignalScribeContext).Assembly);
    }
}
