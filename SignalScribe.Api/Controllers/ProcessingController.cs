using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Api.Controllers.Models;
using SignalScribe.Data;
using SignalScribe.Enums;

namespace SignalScribe.Api.Controllers;

/// <summary>Job-queue visibility: backlog depth, failures, and manual retry. Live worker activity streams over /hubs/status.</summary>
[ApiController]
[Route("api/v0/processing")]
public class ProcessingController(SignalScribeContext db) : ControllerBase
{
    [HttpGet("stats")]
    public async Task<ActionResult<ProcessingStatsDto>> GetStats()
    {
        var byStatus = await db.Jobs
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count);

        var oldestPending = await db.Jobs
            .Where(j => j.Status == JobStatus.Pending)
            .MinAsync(j => (DateTime?)j.CreatedUtc);

        var pendingByType = await db.Jobs
            .Where(j => j.Status == JobStatus.Pending)
            .GroupBy(j => j.Type)
            .Select(g => new TypeCountDto(g.Key, g.Count()))
            .ToListAsync();

        return Ok(new ProcessingStatsDto(
            byStatus.GetValueOrDefault(JobStatus.Pending),
            byStatus.GetValueOrDefault(JobStatus.Leased),
            byStatus.GetValueOrDefault(JobStatus.Completed),
            byStatus.GetValueOrDefault(JobStatus.Failed),
            oldestPending,
            pendingByType));
    }

    [HttpGet("failed")]
    public async Task<ActionResult<List<FailedJobDto>>> GetFailed([FromQuery] int limit = 25)
    {
        limit = Math.Clamp(limit, 1, 100);
        var jobs = await db.Jobs
            .Where(j => j.Status == JobStatus.Failed)
            .OrderByDescending(j => j.CompletedUtc)
            .Take(limit)
            .Select(j => new FailedJobDto(j.Id, j.Type, j.Attempts, j.Error, j.CreatedUtc, j.CompletedUtc))
            .ToListAsync();
        return Ok(jobs);
    }

    [HttpPost("jobs/{id:long}/retry")]
    public async Task<IActionResult> Retry(long id)
    {
        var job = await db.Jobs.FindAsync(id);
        if (job is null)
        {
            return NotFound();
        }

        if (job.Status != JobStatus.Failed)
        {
            return Conflict($"Job {id} is {job.Status}, not Failed.");
        }

        job.Status = JobStatus.Pending;
        job.Attempts = 0;
        job.LeasedBy = null;
        job.LeaseUntilUtc = null;
        job.CompletedUtc = null;
        job.Error = null;
        await db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>
    /// Clears summaries so they are written again — for when the prompt or the model changes, since
    /// a session that already has one is never revisited. Scoped to a session, or to everything.
    /// </summary>
    [HttpPost("resummarize")]
    public async Task<ActionResult<int>> Resummarize([FromQuery] long? sessionId = null)
    {
        var sessions = await db.Sessions
            .Where(s => s.Summary != null && (sessionId == null || s.Id == sessionId))
            .ToListAsync();

        foreach (var session in sessions)
        {
            session.Summary = null;
            session.SummaryModel = null;
        }

        await db.SaveChangesAsync();
        return Ok(sessions.Count);
    }

    /// <summary>
    /// Detaches transmissions from their sessions over a window so sessionization rebuilds them.
    ///
    /// Needed when the clustering rules themselves change: sessions already written keep whatever
    /// shape the old rules gave them, and for a net that shape can be dozens of fragments where
    /// there should be one occurrence. Deliberately scoped to a window rather than offered as a
    /// rebuild-everything button — this deletes session rows, and the summaries attached to them
    /// go with them.
    /// </summary>
    [HttpPost("resessionize")]
    public async Task<ActionResult<ResessionizeResult>> Resessionize(
        [FromQuery] DateTime fromUtc,
        [FromQuery] DateTime toUtc,
        [FromQuery] int? channelId = null)
    {
        if (toUtc <= fromUtc)
        {
            return BadRequest("toUtc must be after fromUtc.");
        }

        var transmissions = await db.Transmissions
            .Where(t => t.StartUtc >= fromUtc && t.StartUtc < toUtc)
            .Where(t => channelId == null || t.ChannelId == channelId)
            .ToListAsync();

        var sessionIds = transmissions
            .Where(t => t.SessionId != null)
            .Select(t => t.SessionId!.Value)
            .Distinct()
            .ToList();

        foreach (var t in transmissions)
        {
            t.SessionId = null;
        }

        // Save the detach first: a session still referenced by a transmission cannot be deleted, and
        // doing both in one change set leaves the order up to the provider.
        await db.SaveChangesAsync();

        var sessions = await db.Sessions.Where(s => sessionIds.Contains(s.Id)).ToListAsync();

        // A session reaching outside the window still has transmissions attached and is not ours to
        // delete; it simply keeps them and re-absorbs nothing.
        var orphaned = sessions.Where(s => !db.Transmissions.Any(t => t.SessionId == s.Id)).ToList();
        db.Sessions.RemoveRange(orphaned);
        await db.SaveChangesAsync();

        return Ok(new ResessionizeResult(transmissions.Count, orphaned.Count));
    }
}

/// <param name="TransmissionsDetached">Transmissions handed back to sessionization.</param>
/// <param name="SessionsRemoved">Sessions that were left with nothing and deleted.</param>
public record ResessionizeResult(int TransmissionsDetached, int SessionsRemoved);
