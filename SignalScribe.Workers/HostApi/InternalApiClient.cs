using System.Net.Http.Json;
using SignalScribe.Contracts;

namespace SignalScribe.Workers.HostApi;

/// <summary>Worker-side client for the host's api/internal data surfaces (job queue lives in JobsClient).</summary>
public sealed class InternalApiClient(HttpClient http)
{
    public async Task<TransmissionInfo?> GetTransmissionAsync(long id, CancellationToken ct) =>
        await http.GetFromJsonAsync<TransmissionInfo>($"api/internal/transmissions/{id}", ct);

    public async Task PostTranscriptAsync(TranscriptIngest ingest, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("api/internal/events/transcripts", ingest, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<SessionFacts?> GetSessionFactsAsync(long id, CancellationToken ct) =>
        await http.GetFromJsonAsync<SessionFacts>($"api/internal/sessions/{id}/facts", ct);

    public async Task PostSessionSummaryAsync(long id, SessionSummaryIngest ingest, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync($"api/internal/sessions/{id}/summary", ingest, ct);
        response.EnsureSuccessStatusCode();
    }
}
