using Microsoft.AspNetCore.Mvc;
using SignalScribe.Contracts;
using SignalScribe.Data;

namespace SignalScribe.Api.Controllers.Internal;

/// <summary>Daemon-facing settings fetch — pulled at startup and on every settingsChanged hub push.</summary>
[ApiController]
[Route("api/internal/settings")]
public class InternalSettingsController(SignalScribeContext db) : ControllerBase
{
    [HttpGet("capture")]
    public async Task<ActionResult<CaptureSettingsDto>> GetCapture() =>
        Ok(SettingsController.ToDto((await db.CaptureSettings.FindAsync(1))!));

    [HttpGet("workers")]
    public async Task<ActionResult<WorkerSettingsDto>> GetWorkers() =>
        Ok(SettingsController.ToDto((await db.WorkerSettings.FindAsync(1))!));
}
