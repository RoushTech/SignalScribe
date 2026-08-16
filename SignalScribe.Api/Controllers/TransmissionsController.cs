using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Api.Controllers.Models;
using SignalScribe.Data;
using SignalScribe.Data.Models;
using SignalScribe.Enums;

namespace SignalScribe.Api.Controllers;

[ApiController]
[Route("api/v0/transmissions")]
public class TransmissionsController(SignalScribeContext db) : ControllerBase
{
    /// <summary>Re-run transcription for one transmission — e.g. after switching to a bigger Whisper model.</summary>
    [HttpPost("{id:long}/reprocess")]
    public async Task<IActionResult> Reprocess(long id)
    {
        if (await db.Transmissions.FindAsync(id) is null)
        {
            return NotFound();
        }

        await EnqueueTranscribeAsync(db, [id]);
        return Ok();
    }

    /// <summary>Bulk re-transcribe: a whole channel, or everything (optionally only what has no transcript yet).</summary>
    [HttpPost("reprocess")]
    public async Task<ActionResult<int>> ReprocessBulk(
        [FromQuery] int? channelId,
        [FromQuery] bool onlyMissing = false,
        [FromQuery] int limit = 500)
    {
        var query = db.Transmissions.Where(t => !t.IsDouble);
        if (channelId is not null)
        {
            query = query.Where(t => t.ChannelId == channelId);
        }

        if (onlyMissing)
        {
            query = query.Where(t => !t.Segments.Any(s => s.Transcript != null && s.Transcript != ""));
        }

        var ids = await query
            .OrderByDescending(t => t.StartUtc)
            .Take(Math.Clamp(limit, 1, 2000))
            .Select(t => t.Id)
            .ToListAsync();

        await EnqueueTranscribeAsync(db, ids);
        return Ok(ids.Count);
    }

    private static async Task EnqueueTranscribeAsync(SignalScribeContext db, List<long> transmissionIds)
    {
        foreach (var id in transmissionIds)
        {
            var payload = $$"""{"transmissionId":{{id}}}""";
            var queued = await db.Jobs.AnyAsync(j =>
                j.Type == JobType.Transcribe && j.PayloadJson == payload &&
                (j.Status == JobStatus.Pending || j.Status == JobStatus.Leased));
            if (!queued)
            {
                db.Jobs.Add(new Job { Type = JobType.Transcribe, PayloadJson = payload, CreatedUtc = DateTime.UtcNow });
            }
        }

        await db.SaveChangesAsync();
    }

    [HttpGet]
    public async Task<ActionResult<List<TransmissionDto>>> GetRecent(
        [FromQuery] int? channelId,
        [FromQuery] DateTime? before,
        [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

        var query = db.Transmissions.AsQueryable();
        if (channelId is not null)
        {
            query = query.Where(t => t.ChannelId == channelId);
        }

        if (before is not null)
        {
            query = query.Where(t => t.StartUtc < before); // cursor paging for "load more"
        }

        var transmissions = await query
            .Include(t => t.Channel)
            .Include(t => t.Segments)
            .OrderByDescending(t => t.StartUtc)
            .Take(limit)
            .ToListAsync();

        return Ok(transmissions.Select(TransmissionMapper.ToDto).ToList());
    }
}
