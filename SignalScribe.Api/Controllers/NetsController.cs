using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Api.Controllers.Models;
using SignalScribe.Data;
using SignalScribe.Data.Models;
using SignalScribe.Enums;

namespace SignalScribe.Api.Controllers;

[ApiController]
[Route("api/v0/nets")]
public class NetsController(SignalScribeContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<NetDto>>> GetAll([FromQuery] int? channelId)
    {
        var query = db.Nets.AsQueryable();
        if (channelId is not null)
        {
            query = query.Where(n => n.ChannelId == channelId);
        }

        var nets = await query
            .Select(n => new
            {
                Net = n,
                SessionCount = n.Sessions.Count,
                LastSessionUtc = n.Sessions.Max(s => (DateTime?)s.StartUtc),
            })
            .OrderBy(n => n.Net.DayOfWeekUtc)
            .ThenBy(n => n.Net.StartTimeUtc)
            .ToListAsync();

        return Ok(nets.Select(n => ToDto(n.Net, n.SessionCount, n.LastSessionUtc)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<NetDto>> Create(NetUpsertRequest request)
    {
        if (await db.Channels.FindAsync(request.ChannelId) is null)
        {
            return NotFound($"Channel {request.ChannelId} does not exist.");
        }

        var net = new Net { Source = NetScheduleSource.Manual };
        Apply(net, request);
        db.Nets.Add(net);
        await db.SaveChangesAsync();
        return Ok(ToDto(net, 0, null));
    }

    [HttpPut("{id:long}")]
    public async Task<ActionResult<NetDto>> Update(long id, NetUpsertRequest request)
    {
        var net = await db.Nets.FindAsync(id);
        if (net is null)
        {
            return NotFound();
        }

        Apply(net, request);
        net.Source = NetScheduleSource.Manual; // a user edit makes the schedule authoritative over mining
        await db.SaveChangesAsync();

        var count = await db.Sessions.CountAsync(s => s.NetId == id);
        var last = await db.Sessions.Where(s => s.NetId == id).MaxAsync(s => (DateTime?)s.StartUtc);
        return Ok(ToDto(net, count, last));
    }

    /// <summary>Deletes the net identity; its session history survives with NetId unlinked (SetNull).</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var net = await db.Nets.FindAsync(id);
        if (net is null)
        {
            return NotFound();
        }

        db.Nets.Remove(net);
        await db.SaveChangesAsync();
        return Ok();
    }

    private static void Apply(Net net, NetUpsertRequest request)
    {
        net.ChannelId = request.ChannelId;
        net.Name = request.Name;
        net.Description = request.Description;
        net.DayOfWeekUtc = request.DayOfWeekUtc;
        net.StartTimeUtc = request.StartTimeUtc;
        net.DurationMinutes = request.DurationMinutes;
    }

    private static NetDto ToDto(Net n, int sessionCount, DateTime? lastSessionUtc) => new(
        n.Id,
        n.ChannelId,
        n.Name,
        n.Description,
        n.Source,
        n.DayOfWeekUtc,
        n.StartTimeUtc,
        n.DurationMinutes,
        sessionCount,
        lastSessionUtc);
}
