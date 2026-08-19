using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SignalScribe.Api.Hubs;
using SignalScribe.Contracts;
using SignalScribe.Data;

namespace SignalScribe.Api.Controllers;

/// <summary>Operator settings. A PUT saves and pushes settingsChanged down the status hub — the daemons re-fetch and apply live.</summary>
[ApiController]
[Route("api/v0/settings")]
public class SettingsController(SignalScribeContext db, IHubContext<StatusHub> hub) : ControllerBase
{
    [HttpGet("capture")]
    public async Task<ActionResult<CaptureSettingsDto>> GetCapture() =>
        Ok(ToDto((await db.CaptureSettings.FindAsync(1))!));

    [HttpPut("capture")]
    public async Task<ActionResult<CaptureSettingsDto>> PutCapture(CaptureSettingsDto dto)
    {
        var settings = (await db.CaptureSettings.FindAsync(1))!;
        settings.CenterFrequencyHz = dto.CenterFrequencyHz;
        settings.SampleRateHz = dto.SampleRateHz;
        settings.ChannelSpacingHz = dto.ChannelSpacingHz;
        settings.GainReductionDb = Math.Clamp(dto.GainReductionDb, 20, 59);
        settings.LnaState = Math.Clamp(dto.LnaState, 0, 3);
        settings.AgcEnabled = dto.AgcEnabled;
        settings.SquelchOpenDb = dto.SquelchOpenDb;
        settings.SquelchCloseDb = dto.SquelchCloseDb;
        settings.SquelchHangMs = dto.SquelchHangMs;
        settings.DeviationHz = Math.Clamp(dto.DeviationHz, 1_000, 15_000);
        settings.MonitorLowHz = dto.MonitorLowHz;
        settings.MonitorHighHz = dto.MonitorHighHz;
        settings.DeviceSerial = string.IsNullOrWhiteSpace(dto.DeviceSerial) ? null : dto.DeviceSerial;
        await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync(HubEvents.SettingsChanged, ServiceNames.Capture);
        return Ok(ToDto(settings));
    }

    [HttpGet("workers")]
    public async Task<ActionResult<WorkerSettingsDto>> GetWorkers() =>
        Ok(ToDto((await db.WorkerSettings.FindAsync(1))!));

    [HttpPut("workers")]
    public async Task<ActionResult<WorkerSettingsDto>> PutWorkers(WorkerSettingsDto dto)
    {
        var settings = (await db.WorkerSettings.FindAsync(1))!;
        settings.WhisperModel = dto.WhisperModel;
        settings.TranscriptionPrompt = dto.TranscriptionPrompt;
        settings.SummaryModel = dto.SummaryModel;
        settings.MaxJobsPerClaim = Math.Clamp(dto.MaxJobsPerClaim, 1, 16);
        settings.TranscriptionThreads = Math.Clamp(dto.TranscriptionThreads, 0, 64);
        settings.SummaryThreads = Math.Clamp(dto.SummaryThreads, 0, 64);
        settings.Paused = dto.Paused;
        settings.DiscardRetentionHours = Math.Clamp(dto.DiscardRetentionHours, 1, 720);
        settings.NoSpeechRetentionHours = Math.Clamp(dto.NoSpeechRetentionHours, 1, 8_760);

        // Capped well under the job lease: gathered jobs are held under lease while they wait, and
        // a gather longer than the lease would hand them to another worker mid-wait.
        settings.TranscriptionGatherSeconds = Math.Clamp(dto.TranscriptionGatherSeconds, 0, 120);
        await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync(HubEvents.SettingsChanged, ServiceNames.Workers);
        return Ok(ToDto(settings));
    }

    /// <summary>Model files present on disk — the settings dropdowns list these rather than a hard-coded set, so what you can pick is what you actually have.</summary>
    [HttpGet("models")]
    public ActionResult<AvailableModelsDto> GetModels([FromServices] IConfiguration config)
    {
        var dir = config.GetValue("ModelsDirectory", "models")!;
        if (!Directory.Exists(dir))
        {
            return Ok(new AvailableModelsDto([], []));
        }

        List<string> Files(string pattern) =>
        [
            .. Directory.EnumerateFiles(dir, pattern)
                .Select(Path.GetFileName)
                .Where(f => f is not null)
                .Select(f => f!)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];

        return Ok(new AvailableModelsDto(Files("*.bin"), Files("*.gguf")));
    }

    internal static CaptureSettingsDto ToDto(Data.Models.CaptureSettings s) => new(
        s.CenterFrequencyHz, s.SampleRateHz, s.ChannelSpacingHz, s.GainReductionDb,
        s.LnaState, s.AgcEnabled, s.SquelchOpenDb, s.SquelchCloseDb, s.SquelchHangMs, s.DeviationHz, s.MonitorLowHz, s.MonitorHighHz, s.DeviceSerial);

    internal static WorkerSettingsDto ToDto(Data.Models.WorkerSettings s) => new(
        s.WhisperModel, s.TranscriptionPrompt, s.SummaryModel, s.MaxJobsPerClaim,
        s.TranscriptionThreads, s.SummaryThreads, s.Paused, s.DiscardRetentionHours,
        s.NoSpeechRetentionHours, s.TranscriptionGatherSeconds);
}
