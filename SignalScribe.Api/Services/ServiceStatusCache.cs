using System.Collections.Concurrent;
using SignalScribe.Contracts;

namespace SignalScribe.Api.Services;

/// <summary>Latest status per service, in memory only (CLAUDE.md). Tracks which connection reports which service so a dropped socket flips that service to offline.</summary>
public class ServiceStatusCache
{
    private readonly ConcurrentDictionary<string, ServiceStatusUpdate> _byService = new();

    private readonly ConcurrentDictionary<string, string> _serviceByConnection = new();

    public void Update(string connectionId, ServiceStatusUpdate update)
    {
        _serviceByConnection[connectionId] = update.Service;
        _byService[update.Service] = update;
    }

    public ServiceStatusUpdate? MarkOffline(string connectionId)
    {
        if (!_serviceByConnection.TryRemove(connectionId, out var service))
        {
            return null; // a browser, not a daemon
        }

        var offline = new ServiceStatusUpdate(service, ServiceStates.Offline, DateTime.UtcNow, []);
        _byService[service] = offline;
        return offline;
    }

    public IReadOnlyList<ServiceStatusUpdate> Snapshot() => _byService.Values.OrderBy(s => s.Service).ToList();
}
