using Microsoft.AspNetCore.Mvc;
using SignalScribe.Data;

namespace SignalScribe.Api.Controllers;

[ApiController]
[Route("api/v0/audio")]
public class AudioController(SignalScribeContext db, IConfiguration config) : ControllerBase
{
    /// <summary>Streams a transmission's Opus/OGG clip with range support so scrubbing works. Browsers play Opus-in-OGG natively.</summary>
    [HttpGet("{transmissionId:long}")]
    public async Task<IActionResult> Get(long transmissionId)
    {
        var transmission = await db.Transmissions.FindAsync(transmissionId);
        if (transmission is null)
        {
            return NotFound();
        }

        // AudioPath comes from the database — resolve against the audio root and refuse anything that escapes it.
        var audioRoot = Path.GetFullPath(config.GetValue("AudioDirectory", "audio")!);
        var fullPath = Path.GetFullPath(Path.Combine(audioRoot, transmission.AudioPath));
        if (!fullPath.StartsWith(audioRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return BadRequest("Audio path escapes the audio root.");
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound("Clip file is missing on disk.");
        }

        return PhysicalFile(fullPath, "audio/ogg", enableRangeProcessing: true);
    }
}
