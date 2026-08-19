using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using SignalScribe.Capture.Spool;
using SignalScribe.Contracts;
using SignalScribe.Enums;

namespace SignalScribe.Capture.HostApi;

/// <summary>Posts ingest events to the web host; spools on any failure so capture never blocks.</summary>
public sealed class HostClient(HttpClient http, EventSpool spool, string audioRoot, ILogger<HostClient> logger)
{
    public async Task PostTransmissionAsync(TransmissionIngest ingest, CancellationToken ct)
    {
        if (!await TryPostAsync(ingest, ct))
        {
            logger.LogWarning("Host unreachable — spooling transmission at {Start} on {Freq} Hz", ingest.StartUtc, ingest.FrequencyHz);
            spool.Append(ingest);
        }
    }

    /// <summary>Reports a rejected clip. Not spooled — if the host is down the file is dropped rather than filling the disk.</summary>
    public async Task PostDiscardAsync(DiscardIngest discard, CancellationToken ct)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/internal/events/discards", discard, ct);
            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // fall through to cleanup
        }

        TryDeleteLocal(discard.AudioPath);
    }

    private void TryDeleteLocal(string relativePath)
    {
        try
        {
            var full = Path.Combine(audioRoot, relativePath);
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not clean up discarded clip: {Message}", ex.Message);
        }
    }

    public Task<int> ReplaySpoolAsync(CancellationToken ct) =>
        spool.ReplayAsync(i => TryPostAsync(i, ct), ct);

    /// <summary>Enabled channel frequencies — the capture daemon's known set for the voice gate. Null when the host is unreachable (keep the previous set).</summary>
    public async Task<HashSet<long>?> GetKnownFrequenciesAsync(CancellationToken ct)
    {
        try
        {
            var freqs = await http.GetFromJsonAsync<List<long>>("api/internal/channels", ct);
            return freqs is null ? null : [.. freqs];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// What each enabled channel is understood to carry. Drives where capture spends its packet
    /// decoders, which unlike the tone detectors are far too costly to run on every open gate.
    /// Null when the host is unreachable — keep the previous map.
    /// </summary>
    /// <summary>Stored squelch references and adaptive flags, so floors outlive a restart.</summary>
    public async Task<List<ChannelSquelchInfo>?> GetChannelSquelchAsync(CancellationToken ct)
    {
        try
        {
            return await http.GetFromJsonAsync<List<ChannelSquelchInfo>>("api/internal/channels/squelch", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reports learned floors. Fire-and-forget by design: this is housekeeping, and a host that is
    /// down must never stall the sample loop — the next report carries the same information.
    /// </summary>
    public async Task PostNoiseFloorsAsync(List<NoiseFloorReport> floors, CancellationToken ct)
    {
        if (floors.Count == 0)
        {
            return;
        }

        try
        {
            await http.PostAsJsonAsync("api/internal/channels/floors", floors, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Floors are re-reported on a timer; a missed one costs nothing.
        }
    }

    public async Task<Dictionary<long, DetectedMode>?> GetChannelModesAsync(CancellationToken ct)
    {
        try
        {
            var modes = await http.GetFromJsonAsync<List<ChannelModeInfo>>("api/internal/channels/modes", ct);
            if (modes is null)
            {
                return null;
            }

            var map = new Dictionary<long, DetectedMode>(modes.Count);
            foreach (var m in modes)
            {
                map[m.FrequencyHz] = m.Mode;
            }

            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<bool> TryPostAsync(TransmissionIngest ingest, CancellationToken ct)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/internal/events/transmissions", ingest, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
