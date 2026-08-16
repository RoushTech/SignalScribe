using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using SignalScribe.Contracts;

namespace SignalScribe.Capture.Settings;

/// <summary>
/// Current operator settings, fetched from the host at startup and re-fetched on every
/// settingsChanged hub push. Null until the first successful fetch — the pipeline falls back to
/// appsettings defaults so capture never blocks on the host.
/// </summary>
public sealed class CaptureSettingsProvider(IHttpClientFactory httpFactory, ILogger<CaptureSettingsProvider> logger)
{
    public CaptureSettingsDto? Current { get; private set; }

    /// <summary>Raised after a successful re-fetch. The pipeline decides retune-vs-live-apply from the delta.</summary>
    public event Action<CaptureSettingsDto>? Changed;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var http = httpFactory.CreateClient(nameof(CaptureSettingsProvider));
            var dto = await http.GetFromJsonAsync<CaptureSettingsDto>("api/internal/settings/capture", ct);
            if (dto is not null)
            {
                Current = dto;
                logger.LogInformation("Capture settings refreshed: {Center} Hz @ {Rate} SPS, gRdB {Gain}, AGC {Agc}",
                    dto.CenterFrequencyHz, dto.SampleRateHz, dto.GainReductionDb, dto.AgcEnabled);
                Changed?.Invoke(dto);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Settings fetch failed ({Message}) — keeping current values", ex.Message);
        }
    }
}
