using System.Net.Http.Json;
using SignalScribe.Contracts;

namespace SignalScribe.Workers.Settings;

/// <summary>Current worker settings — fetched at startup, re-fetched on settingsChanged hub pushes. Defaults apply until the first successful fetch.</summary>
public sealed class WorkerSettingsProvider(IHttpClientFactory httpFactory, ILogger<WorkerSettingsProvider> logger)
{
    public WorkerSettingsDto Current { get; private set; } = new(
        WhisperModel: "ggml-small.en-q5_1.bin",
        TranscriptionPrompt: string.Empty,
        SummaryModel: "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf",
        MaxJobsPerClaim: 4,
        TranscriptionThreads: 0,
        SummaryThreads: 0,
        Paused: false,
        DiscardRetentionHours: 24,
        NoSpeechRetentionHours: 72,
        TranscriptionGatherSeconds: 20);

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var http = httpFactory.CreateClient(nameof(WorkerSettingsProvider));
            var dto = await http.GetFromJsonAsync<WorkerSettingsDto>("api/internal/settings/workers", ct);
            if (dto is not null)
            {
                Current = dto;
                logger.LogInformation("Worker settings refreshed: whisper={Whisper}, maxJobs={Max}, paused={Paused}",
                    dto.WhisperModel, dto.MaxJobsPerClaim, dto.Paused);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Settings fetch failed ({Message}) — keeping current values", ex.Message);
        }
    }
}
