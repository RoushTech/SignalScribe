using Microsoft.AspNetCore.SignalR;
using SignalScribe.Api.Services;
using SignalScribe.Contracts;

namespace SignalScribe.Api.Hubs;

/// <summary>
/// Status relay: capture/workers connect as SignalR clients and push updates; browsers connect
/// to the same hub and receive them live. Latest-per-service is cached in memory only.
/// </summary>
public class StatusHub(ServiceStatusCache cache, SpectrumCache spectrum) : Hub
{
    /// <summary>Called by daemons. Caches and relays to every other client (the browsers).</summary>
    public async Task ReportStatus(ServiceStatusUpdate update)
    {
        cache.Update(Context.ConnectionId, update);
        await Clients.Others.SendAsync(HubEvents.StatusChanged, update);
    }

    /// <summary>Called by the capture daemon at ~10 Hz. Relayed straight to browsers for the waterfall.</summary>
    public async Task ReportSpectrum(SpectrumRow row)
    {
        spectrum.Latest = row;
        await Clients.Others.SendAsync(HubEvents.Spectrum, row);
    }

    /// <summary>Called by browsers on connect to render current state before the next push.</summary>
    public IReadOnlyList<ServiceStatusUpdate> GetSnapshot() => cache.Snapshot();

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // A daemon dropping its socket is itself a status change worth showing immediately.
        if (cache.MarkOffline(Context.ConnectionId) is { } offline)
        {
            await Clients.Others.SendAsync(HubEvents.StatusChanged, offline);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
