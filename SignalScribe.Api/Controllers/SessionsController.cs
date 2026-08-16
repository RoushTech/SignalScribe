using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Api.Controllers.Models;
using SignalScribe.Data;

namespace SignalScribe.Api.Controllers;

[ApiController]
[Route("api/v0/sessions")]
public class SessionsController(SignalScribeContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<SessionDto>>> GetAll(
        [FromQuery] int? channelId,
        [FromQuery] long? netId,
        [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        var query = db.Sessions.AsQueryable();
        if (channelId is not null)
        {
            query = query.Where(s => s.ChannelId == channelId);
        }

        if (netId is not null)
        {
            query = query.Where(s => s.NetId == netId);
        }

        var sessions = await query
            .OrderByDescending(s => s.StartUtc)
            .Take(limit)
            .Select(s => new SessionDto(
                s.Id,
                s.ChannelId,
                s.Channel.Label,
                s.StartUtc,
                s.EndUtc,
                s.IsNet,
                s.NetId,
                s.Net != null ? s.Net.Name : null,
                s.Transmissions.Count,
                s.Summary,
                s.SummaryModel))
            .ToListAsync();

        return Ok(sessions);
    }
}
