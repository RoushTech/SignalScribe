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
}
