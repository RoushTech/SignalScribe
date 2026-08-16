using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Api.Controllers.Models;
using SignalScribe.Api.Services;
using SignalScribe.Data;

namespace SignalScribe.Api.Controllers;

/// <summary>Review surface for clips the capture gate rejected — why it rejected them, and the audio to judge for yourself.</summary>
[ApiController]
[Route("api/v0/discards")]
public class DiscardsController(SignalScribeContext db, IConfiguration config) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<DiscardDto>>> GetRecent([FromQuery] int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        var clips = await db.DiscardedClips
            .OrderByDescending(d => d.StartUtc)
            .Take(limit)
            .Select(d => new DiscardDto(
                d.Id,
                d.FrequencyHz,
                d.StartUtc,
                (int)(d.EndUtc - d.StartUtc).TotalMilliseconds,
                d.Reason,
                d.PeakDbfs,
                d.VoicedMs,
                d.SpeechBandRatio,
                d.ModulationDepth,
                d.SyllableRateHz,
                d.SustainedTone))
            .ToListAsync();
        return Ok(clips);
    }

    [HttpGet("stats")]
    public async Task<ActionResult<DiscardStatsDto>> GetStats()
    {
        var total = await db.DiscardedClips.CountAsync();
        var oldest = await db.DiscardedClips.MinAsync(d => (DateTime?)d.StartUtc);
        var byReason = await db.DiscardedClips
            .GroupBy(d => d.Reason)
            .Select(g => new ReasonCountDto(g.Key, g.Count()))
            .ToListAsync();
        return Ok(new DiscardStatsDto(total, oldest, byReason));
    }

    /// <summary>Audio for a rejected clip (separate from the transmission audio route — these are not transmissions).</summary>
    [HttpGet("{id:long}/audio")]
    public async Task<IActionResult> GetAudio(long id)
    {
        var clip = await db.DiscardedClips.FindAsync(id);
        if (clip is null)
        {
            return NotFound();
        }

        var audioRoot = Path.GetFullPath(config.GetValue("AudioDirectory", "audio")!);
        var fullPath = Path.GetFullPath(Path.Combine(audioRoot, clip.AudioPath));
        if (!fullPath.StartsWith(audioRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return BadRequest("Audio path escapes the audio root.");
        }

        return System.IO.File.Exists(fullPath)
            ? PhysicalFile(fullPath, "audio/ogg", enableRangeProcessing: true)
            : NotFound("Clip file is missing (already purged).");
    }

    /// <summary>Purge now, rather than waiting for the retention sweep.</summary>
    [HttpDelete]
    public async Task<ActionResult<int>> PurgeAll([FromServices] DiscardPurgeService purge) =>
        Ok(await purge.PurgeAsync(DateTime.UtcNow, HttpContext.RequestAborted));
}
