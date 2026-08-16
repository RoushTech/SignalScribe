using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Data;

namespace SignalScribe.Api.Controllers.Internal;

/// <summary>Daemon-facing channel list — the capture daemon's "known frequency" set for the voice gate (known channels record everything; unknown frequencies must pass the voice gate).</summary>
[ApiController]
[Route("api/internal/channels")]
public class InternalChannelsController(SignalScribeContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<long>>> GetFrequencies() =>
        Ok(await db.Channels.Where(c => c.Enabled).Select(c => c.FrequencyHz).ToListAsync());
}
