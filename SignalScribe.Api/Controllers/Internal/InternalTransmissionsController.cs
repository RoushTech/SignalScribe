using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Contracts;
using SignalScribe.Data;

namespace SignalScribe.Api.Controllers.Internal;

[ApiController]
[Route("api/internal/transmissions")]
public class InternalTransmissionsController(SignalScribeContext db) : ControllerBase
{
    [HttpGet("{id:long}")]
    public async Task<ActionResult<TransmissionInfo>> Get(long id)
    {
        var t = await db.Transmissions.Include(x => x.Channel).Include(x => x.Markers).FirstOrDefaultAsync(x => x.Id == id);
        return t is null
            ? NotFound()
            : Ok(new TransmissionInfo(
                t.Id,
                t.Channel.FrequencyHz,
                t.AudioPath,
                t.IsDouble,
                t.Markers.OrderBy(m => m.OffsetMs).Select(m => new MarkerInfo(m.Type, m.OffsetMs)).ToList()));
    }
}
