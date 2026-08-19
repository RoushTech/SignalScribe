using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Contracts;
using SignalScribe.Data;
using SignalScribe.Enums;

namespace SignalScribe.Api.Controllers.Internal;

[ApiController]
[Route("api/internal/channels")]
public class InternalChannelsController(SignalScribeContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<long>>> GetFrequencies() =>
        Ok(await db.Channels.Where(c => c.Enabled).Select(c => c.FrequencyHz).ToListAsync());

    /// <summary>
    /// Enabled channels with the mode each is known to carry, so the capture daemon can decide where
    /// to spend the expensive decoders. The soft TNC costs ~4.7% of a core per open gate — forty times
    /// the tone detector — so unlike CTCSS it cannot simply run everywhere, and capture needs to know
    /// which frequencies are worth it.
    /// </summary>
    /// <summary>
    /// Squelch references for the enabled channels, so a restarting daemon begins from the floors it
    /// had learned rather than from silence — and so a floor the operator has pinned is honoured.
    /// </summary>
    [HttpGet("squelch")]
    public async Task<ActionResult<List<ChannelSquelchInfo>>> GetSquelch() =>
        Ok(await db.Channels
            .Where(c => c.Enabled)
            .Select(c => new ChannelSquelchInfo(c.FrequencyHz, c.NoiseFloorDbfs, c.AdaptiveSquelch))
            .ToListAsync());

    /// <summary>
    /// Floors learned by capture. Idempotent and lossy by design: this is the daemon's current best
    /// estimate, overwritten each time, and a channel whose floor the operator pinned is skipped so
    /// the daemon cannot argue with them.
    /// </summary>
    [HttpPost("floors")]
    public async Task<IActionResult> ReportFloors(List<NoiseFloorReport> floors)
    {
        if (floors.Count == 0)
        {
            return Ok();
        }

        var byFrequency = floors.ToDictionary(f => f.FrequencyHz, f => f.NoiseFloorDbfs);
        var channels = await db.Channels
            .Where(c => byFrequency.Keys.Contains(c.FrequencyHz) && c.AdaptiveSquelch)
            .ToListAsync();

        foreach (var channel in channels)
        {
            channel.NoiseFloorDbfs = Math.Round(byFrequency[channel.FrequencyHz], 1);
        }

        await db.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("modes")]
    public async Task<ActionResult<List<ChannelModeInfo>>> GetModes()
    {
        // LearnedState is a JSON column deserialised client-side, so materialise first.
        var channels = await db.Channels
            .Where(c => c.Enabled)
            .Select(c => new { c.FrequencyHz, c.Modulation, c.LearnedStateJson })
            .ToListAsync();

        return Ok(channels
            .Select(c => new ChannelModeInfo(
                c.FrequencyHz,
                c.Modulation ?? Data.Models.Channel.ParseLearnedMode(c.LearnedStateJson) ?? DetectedMode.Unknown))
            .ToList());
    }
}
