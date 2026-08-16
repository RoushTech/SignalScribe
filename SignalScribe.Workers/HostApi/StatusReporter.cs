using Microsoft.AspNetCore.SignalR.Client;
using SignalScribe.Contracts;
using SignalScribe.Workers.Settings;

namespace SignalScribe.Workers.HostApi;

/// <summary>Holds a SignalR socket to the web host: streams worker status out (~5s cadence), receives settingsChanged pushes in.</summary>
public sealed class StatusReporter(
    IConfiguration config,
    WorkerSettingsProvider settings,
    ILogger<StatusReporter> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>Set by JobPollerService; read by the report loop.</summary>
    public static volatile WorkerStatusSnapshot Snapshot = new("idle", null, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hubUrl = $"{config.GetValue("Host:BaseUrl", "http://localhost:5020")!.TrimEnd('/')}/hubs/status";
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        connection.On<string>(HubEvents.SettingsChanged, service =>
        {
            if (service == ServiceNames.Workers)
            {
                _ = settings.RefreshAsync(stoppingToken);
            }
        });

        await using (connection)
        {
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

                    var s = Snapshot;
                    await connection.InvokeAsync(
                        "ReportStatus",
                        new ServiceStatusUpdate(ServiceNames.Workers, s.State, DateTime.UtcNow, new Dictionary<string, string>
                        {
                            ["currentJob"] = s.CurrentJob ?? "none",
                            ["jobsCompleted"] = s.JobsCompleted.ToString(),
                        }),
                        stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug("Status push failed ({Message}) — will retry", ex.Message);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
    }
}

public record WorkerStatusSnapshot(string State, string? CurrentJob, long JobsCompleted);
