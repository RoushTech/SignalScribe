using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using SignalScribe.Contracts;
using SignalScribe.Enums;
using SignalScribe.Workers.HostApi;
using SignalScribe.Workers.Settings;

namespace SignalScribe.Workers.Handlers;

/// <summary>
/// LLamaSharp narrative summaries. The facts (roster, NCS candidates, durations) are computed by
/// the host from the database — the LLM only writes prose from them (CLAUDE.md invariant).
/// Weights are loaded per job and disposed after, reclaiming the multi-GB working set between runs.
/// </summary>
public sealed class SummaryHandler(
    IConfiguration config,
    InternalApiClient api,
    WorkerSettingsProvider settings,
    ILogger<SummaryHandler> logger) : IJobHandler
{
    public JobType Type => JobType.Summarize;

    public async Task ExecuteAsync(ClaimedJob job, CancellationToken ct)
    {
        var sessionId = JsonDocument.Parse(job.PayloadJson).RootElement.GetProperty("sessionId").GetInt64();
        var facts = await api.GetSessionFactsAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        var modelFile = settings.Current.SummaryModel;
        var modelPath = Path.Combine(config.GetValue("Workers:ModelsDirectory", "models")!, modelFile);
        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException($"Summary model not found: {modelPath} (run scripts/download-models.sh)");
        }

        var prompt = SignalScribe.Analysis.SummaryPrompt.Build(facts);
        var threads = SignalScribe.Analysis.WorkerThreads.Resolve(settings.Current.SummaryThreads);
        var parameters = new ModelParams(modelPath)
        {
            ContextSize = 8192,
            GpuLayerCount = 0,
            Threads = threads,
            BatchThreads = threads,
        };

        string summary;
        using (var weights = LLamaWeights.LoadFromFile(parameters))
        {
            var executor = new StatelessExecutor(weights, parameters);
            var inference = new InferenceParams
            {
                // Scaled with the material, like the requested length — a fixed ceiling silently
                // truncated the long sessions that most needed the room.
                MaxTokens = SignalScribe.Analysis.SummaryPrompt.MaxTokens(facts.Transcript.Length),
                AntiPrompts = ["<|im_end|>"],
            };

            var sb = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inference, ct))
            {
                sb.Append(token);
            }

            summary = sb.ToString().Replace("<|im_end|>", "").Trim();
        }

        if (summary.Length == 0)
        {
            throw new InvalidOperationException("LLM produced an empty summary");
        }

        await api.PostSessionSummaryAsync(sessionId, new SessionSummaryIngest($"llama.cpp/{modelFile}", summary), ct);
        logger.LogInformation("Summarized session {Id} ({Chars} chars)", sessionId, summary.Length);
    }
}
