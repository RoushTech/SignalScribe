using Microsoft.AspNetCore.SignalR.Client;
using SignalScribe.Capture.Settings;
using SignalScribe.Contracts;

namespace SignalScribe.Capture.HostApi;

/// <summary>
/// Holds a SignalR socket to the web host: streams capture status out (~5s cadence) and receives
/// settingsChanged pushes in. Fire-and-forget by design: status must never couple the capture
/// pipeline to the host.
/// </summary>
public sealed class StatusReporter(
    IConfiguration config,
    CaptureSettingsProvider settings,
    ILogger<StatusReporter> logger) : BackgroundService
{
    // Fast tick drains the spectrum queue (~10 Hz rows); status goes every StatusEveryTicks.
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    // 1 s cadence: the dashboard's live recording indicators need to feel immediate.
    private const int StatusEveryTicks = 10;

    /// <summary>Set by CaptureService; read by the report loop.</summary>
    public static volatile CaptureStatusSnapshot Snapshot = new("initializing", null, 0, 0, 0, [], []);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hubUrl = $"{config.GetValue("Host:BaseUrl", "http://localhost:5020")!.TrimEnd('/')}/hubs/status";
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        connection.On<string>(HubEvents.SettingsChanged, service =>
        {
            if (service == ServiceNames.Capture)
            {
                _ = settings.RefreshAsync(stoppingToken);
            }
        });

        await using (connection)
        {
            var tick = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (connection.State == HubConnectionState.Disconnected)
                    {
                        await connection.StartAsync(stoppingToken);
                        logger.LogInformation("Status socket connected to {Url}", hubUrl);
                        // (Re)connect implies the host is reachable — sync settings we may have
                        // missed while down (covers host-not-yet-up at daemon startup).
                        await settings.RefreshAsync(stoppingToken);
                    }

                    while (SpectrumPublisher.TryDequeue(out var row))
                    {
                        await connection.InvokeAsync("ReportSpectrum", row, stoppingToken);
                    }

                    if (tick++ % StatusEveryTicks == 0)
                    {
                        var s = Snapshot;
                        await connection.InvokeAsync(
                            "ReportStatus",
                            new ServiceStatusUpdate(
                                ServiceNames.Capture,
                                s.State,
                                DateTime.UtcNow,
                                new Dictionary<string, string>
                                {
                                    ["source"] = s.Source ?? "none",
                                    ["sampleCounter"] = s.SampleCounter.ToString(),
                                    ["openGates"] = s.OpenGates.ToString(),
                                    ["adcOverloads"] = s.AdcOverloads.ToString(),
                                },
                                s.Devices.ToList(),
                                s.Gates.ToList()),
                            stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug("Status push failed ({Message}) — will retry", ex.Message);
                }

                await Task.Delay(Tick, stoppingToken);
            }
        }
    }
}

public record CaptureStatusSnapshot(
    string State,
    string? Source,
    long SampleCounter,
    int OpenGates,
    long AdcOverloads,
    IReadOnlyList<SdrDeviceInfo> Devices,
    IReadOnlyList<ActiveGate> Gates);
