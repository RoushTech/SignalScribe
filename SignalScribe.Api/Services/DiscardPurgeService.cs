using Microsoft.EntityFrameworkCore;
using SignalScribe.Data;

namespace SignalScribe.Api.Services;

/// <summary>
/// Retention sweep for rejected clips: deletes rows and their audio files once they age past the
/// operator's retention window. Runs hourly in the web host (the single DB writer) — a timer here
/// rather than a second job framework, since the existing job queue covers worker-side retries and
/// this is one periodic housekeeping task.
/// </summary>
public sealed class DiscardPurgeService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<DiscardPurgeService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short initial delay keeps startup light and lets migrations finish first.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = await PurgeAsync(null, stoppingToken);
                if (removed > 0)
                {
                    logger.LogInformation("Purged {Count} expired discarded clips", removed);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Discard purge failed");
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }
    }

    /// <summary>Removes expired discards, or everything when <paramref name="purgeBefore"/> is now (manual purge).</summary>
    public async Task<int> PurgeAsync(DateTime? purgeBefore, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SignalScribeContext>();

        var cutoff = purgeBefore ?? DateTime.UtcNow.AddHours(-await RetentionHoursAsync(db, ct));
        var expired = await db.DiscardedClips.Where(d => d.StartUtc < cutoff).ToListAsync(ct);
        if (expired.Count == 0)
        {
            return 0;
        }

        var audioRoot = Path.GetFullPath(config.GetValue("AudioDirectory", "audio")!);
        foreach (var clip in expired)
        {
            TryDeleteFile(audioRoot, clip.AudioPath);
        }

        db.DiscardedClips.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    private static async Task<int> RetentionHoursAsync(SignalScribeContext db, CancellationToken ct)
    {
        var settings = await db.WorkerSettings.FindAsync([1], ct);
        return Math.Clamp(settings?.DiscardRetentionHours ?? 24, 1, 720);
    }

    private void TryDeleteFile(string audioRoot, string relativePath)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(audioRoot, relativePath));
            if (full.StartsWith(audioRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not delete discarded clip {Path}: {Message}", relativePath, ex.Message);
        }
    }
}
