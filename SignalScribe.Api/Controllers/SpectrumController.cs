using Microsoft.AspNetCore.Mvc;
using SignalScribe.Api.Services;
using SignalScribe.Contracts;

namespace SignalScribe.Api.Controllers;

[ApiController]
[Route("api/v0/spectrum")]
public class SpectrumController(SpectrumCache cache) : ControllerBase
{
    /// <summary>Latest waterfall row — browser initial paint; live rows stream over /hubs/status.</summary>
    [HttpGet("latest")]
    public ActionResult<SpectrumRow> GetLatest() =>
        cache.Latest is { } row ? Ok(row) : NotFound();
}
