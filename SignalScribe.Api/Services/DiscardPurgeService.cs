using Microsoft.EntityFrameworkCore;
using SignalScribe.Analysis;
using SignalScribe.Data;
using SignalScribe.Enums;

namespace SignalScribe.Api.Services;

/// <summary>
/// Retention sweep for clips with nothing in them: rejected clips, and kept recordings that settled
/// as empty (see <see cref="NoSpeechRetention"/>). Each ages past its own operator-set window, then
/// rows and audio files go together. Runs hourly in the web host (the single DB writer) — a timer
/// here rather than a second job framework, since the existing job queue covers worker-side retries
/// and this is one periodic housekeeping task.
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

                var emptied = await PurgeNoSpeechAsync(stoppingToken);
                if (emptied > 0)
                {
                    logger.LogInformation("Purged {Count} expired no-speech recordings", emptied);
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

    /// <summary>
    /// Removes kept recordings that settled as empty and aged past the no-speech window. The SQL
    /// narrows; <see cref="NoSpeechRetention.IsPurgeable"/> decides — one definition of "empty",
    /// testable on its own, with the query only ever a superset of it.
    /// </summary>
    public async Task<int> PurgeNoSpeechAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SignalScribeContext>();

        var settings = await db.WorkerSettings.FindAsync([1], ct);
        var hours = Math.Clamp(settings?.NoSpeechRetentionHours ?? 72, 1, 8_760);
        var cutoff = DateTime.UtcNow.AddHours(-hours);

        var candidates = await db.Transmissions
            .Include(t => t.Segments)
            .Where(t => t.StartUtc < cutoff
                && (t.Mode == DetectedMode.AnalogFm || t.Mode == DetectedMode.Unknown)
                && !t.Segments.Any(s => s.Transcript != null && s.Transcript != "")
                && (t.TranscribedByModel != null || t.VoicedMs < NoSpeechRetention.MinVoicedMs))
            .ToListAsync(ct);

        var expired = candidates
            .Where(t => NoSpeechRetention.IsPurgeable(
                t.Mode,
                transcribed: t.TranscribedByModel is not null,
                t.VoicedMs,
                anySegmentHasText: t.Segments.Any(s => !string.IsNullOrWhiteSpace(s.Transcript))))
            .ToList();
        if (expired.Count == 0)
        {
            return 0;
        }

        var audioRoot = Path.GetFullPath(config.GetValue("AudioDirectory", "audio")!);
        foreach (var transmission in expired)
        {
            TryDeleteFile(audioRoot, transmission.AudioPath);
        }

        db.Transmissions.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired.Count;
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
