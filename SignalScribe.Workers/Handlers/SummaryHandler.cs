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

        var prompt = BuildPrompt(facts);
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
                MaxTokens = 400,
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

    /// <summary>ChatML prompt (Qwen-style). Facts are authoritative; the model is told not to invent beyond them.</summary>
    private static string BuildPrompt(SessionFacts facts)
    {
        var transcript = facts.Transcript.Length > 24_000 ? facts.Transcript[..24_000] + "\n[transcript truncated]" : facts.Transcript;
        var kind = facts.IsNet ? $"an amateur radio net{(facts.NetName is null ? "" : $" (\"{facts.NetName}\")")}" : "an amateur radio conversation";
        var roster = facts.Callsigns.Count > 0 ? string.Join(", ", facts.Callsigns) : "none identified";

        return $"""
            <|im_start|>system
            You summarize amateur radio activity logs. Write a concise narrative summary (3-6 sentences) of the session below.
            Only state facts supported by the transcript and metadata. Do not invent callsigns, names, or events.<|im_end|>
            <|im_start|>user
            Session: {kind} on {facts.ChannelLabel} ({facts.FrequencyHz / 1_000_000.0:F4} MHz)
            Start: {facts.StartUtc:yyyy-MM-dd HH:mm} UTC, duration: {(facts.EndUtc is null ? "unknown" : $"{(facts.EndUtc.Value - facts.StartUtc).TotalMinutes:F0} minutes")}
            Transmissions: {facts.TransmissionCount}
            Callsigns heard: {roster}

            Transcript:
            {transcript}<|im_end|>
            <|im_start|>assistant
            """;
    }
}
