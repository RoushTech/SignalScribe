using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Api.Controllers.Models;
using SignalScribe.Data;
using SignalScribe.Data.Models;

namespace SignalScribe.Api.Controllers;

[ApiController]
[Route("api/v0/channels")]
public class ChannelsController(SignalScribeContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ChannelDto>>> GetAll()
    {
        var channels = await db.Channels
            .Select(c => new
            {
                Channel = c,
                TransmissionCount = c.Transmissions.Count,
                LastHeardUtc = c.Transmissions.Max(t => (DateTime?)t.StartUtc),
            })
            .OrderBy(c => c.Channel.FrequencyHz)
            .ToListAsync();

        return Ok(channels.Select(c => ToDto(c.Channel, c.TransmissionCount, c.LastHeardUtc)).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ChannelDto>> Get(int id)
    {
        var channel = await db.Channels.FindAsync(id);
        if (channel is null)
        {
            return NotFound();
        }

        var count = await db.Transmissions.CountAsync(t => t.ChannelId == id);
        var last = await db.Transmissions.Where(t => t.ChannelId == id).MaxAsync(t => (DateTime?)t.StartUtc);
        return Ok(ToDto(channel, count, last));
    }

    /// <summary>Manually pre-create a channel before it's ever heard (ingest auto-creates the rest).</summary>
    [HttpPost]
    public async Task<ActionResult<ChannelDto>> Create(ChannelUpsertRequest request)
    {
        if (await db.Channels.AnyAsync(c => c.FrequencyHz == request.FrequencyHz))
        {
            return Conflict($"A channel already exists at {request.FrequencyHz} Hz.");
        }

        var channel = new Channel();
        Apply(channel, request);
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return Ok(ToDto(channel, 0, null));
    }

    /// <summary>Frequency is detected but adjustable — everything here is user-editable. Learned state is not touched.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<ChannelDto>> Update(int id, ChannelUpsertRequest request)
    {
        var channel = await db.Channels.FindAsync(id);
        if (channel is null)
        {
            return NotFound();
        }

        if (await db.Channels.AnyAsync(c => c.Id != id && c.FrequencyHz == request.FrequencyHz))
        {
            return Conflict($"A channel already exists at {request.FrequencyHz} Hz.");
        }

        Apply(channel, request);
        await db.SaveChangesAsync();

        var count = await db.Transmissions.CountAsync(t => t.ChannelId == id);
        var last = await db.Transmissions.Where(t => t.ChannelId == id).MaxAsync(t => (DateTime?)t.StartUtc);
        return Ok(ToDto(channel, count, last));
    }

    private static void Apply(Channel channel, ChannelUpsertRequest request)
    {
        channel.FrequencyHz = request.FrequencyHz;
        channel.Label = request.Label;
        channel.Type = request.Type;
        channel.Enabled = request.Enabled;
        channel.Callsign = request.Callsign;
        channel.Description = request.Description;
        channel.SetSquelchTone(request.CtcssToneHz, request.DcsCode);
        channel.Notes = request.Notes;
        channel.Modulation = request.Modulation;
        channel.AdaptiveSquelch = request.AdaptiveSquelch;

        // A pinned floor is the operator's to set; an adaptive one belongs to the daemon and must
        // not be overwritten from a settings form that was rendered minutes ago.
        if (!request.AdaptiveSquelch && request.NoiseFloorDbfs is { } floor)
        {
            channel.NoiseFloorDbfs = floor;
        }
    }

    private static ChannelDto ToDto(Channel c, int transmissionCount, DateTime? lastHeardUtc) => new(
        c.Id,
        c.FrequencyHz,
        c.Label,
        c.Type,
        c.Enabled,
        c.Callsign,
        c.Description,
        c.CtcssToneHz,
        c.DcsCode,
        c.Notes,
        c.NoiseFloorDbfs,
        c.AdaptiveSquelch,
        c.LearnedState?.CtcssToneHz,
        c.LearnedState?.DcsCode,
        c.Modulation,
        c.LearnedState?.Mode,
        c.LearnedState?.ModeUpdatedUtc,
        transmissionCount,
        lastHeardUtc,
        c.AutoDisabledReason,
        c.LastSpeechUtc);
}
