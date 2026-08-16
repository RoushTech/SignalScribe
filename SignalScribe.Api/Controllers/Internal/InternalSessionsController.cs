using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Contracts;
using SignalScribe.Data;

namespace SignalScribe.Api.Controllers.Internal;

[ApiController]
[Route("api/internal/sessions")]
public class InternalSessionsController(SignalScribeContext db) : ControllerBase
{
    /// <summary>Deterministic session facts for the summary LLM — roster, counts, and transcript come from the database, never from the model.</summary>
    [HttpGet("{id:long}/facts")]
    public async Task<ActionResult<SessionFacts>> GetFacts(long id)
    {
        var session = await db.Sessions
            .Include(s => s.Channel)
            .Include(s => s.Net)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (session is null)
        {
            return NotFound();
        }

        var segments = await db.Segments
            .Where(s => s.Transmission.SessionId == id && s.Transcript != null && s.Transcript != "")
            .OrderBy(s => s.Transmission.StartUtc)
            .ThenBy(s => s.StartMs)
            .Select(s => new { s.Transmission.StartUtc, s.Transcript, s.Callsign })
            .ToListAsync();

        var callsigns = segments
            .Where(s => s.Callsign != null)
            .Select(s => s.Callsign!)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        var transcript = new StringBuilder();
        foreach (var seg in segments)
        {
            transcript.AppendLine($"[{seg.StartUtc:HH:mm:ss}{(seg.Callsign is null ? "" : $" {seg.Callsign}")}] {seg.Transcript!.Trim()}");
        }

        var transmissionCount = await db.Transmissions.CountAsync(t => t.SessionId == id);

        return Ok(new SessionFacts(
            session.Id,
            session.Channel.Label,
            session.Channel.FrequencyHz,
            session.IsNet,
            session.Net?.Name,
            session.StartUtc,
            session.EndUtc,
            transmissionCount,
            callsigns,
            transcript.ToString()));
    }

    [HttpPost("{id:long}/summary")]
    public async Task<IActionResult> PostSummary(long id, SessionSummaryIngest ingest)
    {
        var session = await db.Sessions.FindAsync(id);
        if (session is null)
        {
            return NotFound();
        }

        session.Summary = ingest.Summary;
        session.SummaryModel = ingest.Model;
        await db.SaveChangesAsync();
        return Ok();
    }
}
