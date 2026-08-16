using System.Net.Http.Json;
using SignalScribe.Contracts;
using SignalScribe.Enums;

namespace SignalScribe.Workers.HostApi;

public sealed class JobsClient(HttpClient http)
{
    public async Task<List<ClaimedJob>> ClaimAsync(string workerId, JobType[] types, int maxJobs, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            "api/internal/jobs/claim", new JobClaimRequest(workerId, types, maxJobs), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ClaimedJob>>(ct) ?? [];
    }

    public async Task CompleteAsync(long jobId, string workerId, bool success, string? error, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(
            $"api/internal/jobs/{jobId}/complete", new JobCompleteRequest(workerId, success, error), ct);
        response.EnsureSuccessStatusCode();
    }
}
