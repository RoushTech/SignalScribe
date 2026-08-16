using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Contracts;
using SignalScribe.Data;
using SignalScribe.Enums;

namespace SignalScribe.Api.Controllers.Internal;

/// <summary>Job queue for the worker process: lease-based claims, idempotent completion.</summary>
[ApiController]
[Route("api/internal/jobs")]
public class JobsController(SignalScribeContext db) : ControllerBase
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    private const int MaxAttempts = 3;

    [HttpPost("claim")]
    public async Task<ActionResult<List<ClaimedJob>>> Claim(JobClaimRequest request)
    {
        var now = DateTime.UtcNow;
        var max = Math.Clamp(request.MaxJobs, 1, 16);

        var jobs = await db.Jobs
            .Where(j => request.Types.Contains(j.Type))
            .Where(j => j.Status == JobStatus.Pending
                || (j.Status == JobStatus.Leased && j.LeaseUntilUtc < now))
            .OrderBy(j => j.Id)
            .Take(max)
            .ToListAsync();

        foreach (var job in jobs)
        {
            job.Status = JobStatus.Leased;
            job.LeasedBy = request.WorkerId;
            job.LeaseUntilUtc = now + LeaseDuration;
            job.Attempts++;
        }

        await db.SaveChangesAsync();
        return Ok(jobs.Select(j => new ClaimedJob(j.Id, j.Type, j.PayloadJson)).ToList());
    }

    [HttpPost("{id:long}/complete")]
    public async Task<IActionResult> Complete(long id, JobCompleteRequest request)
    {
        var job = await db.Jobs.FindAsync(id);
        if (job is null)
        {
            return NotFound();
        }

        // Idempotent: completing an already-completed job is a no-op.
        if (job.Status == JobStatus.Completed)
        {
            return Ok();
        }

        if (request.Success)
        {
            job.Status = JobStatus.Completed;
            job.CompletedUtc = DateTime.UtcNow;
            job.Error = null;
        }
        else if (job.Attempts >= MaxAttempts)
        {
            job.Status = JobStatus.Failed;
            job.CompletedUtc = DateTime.UtcNow;
            job.Error = request.Error;
        }
        else
        {
            job.Status = JobStatus.Pending;
            job.LeasedBy = null;
            job.LeaseUntilUtc = null;
            job.Error = request.Error;
        }

        await db.SaveChangesAsync();
        return Ok();
    }
}
