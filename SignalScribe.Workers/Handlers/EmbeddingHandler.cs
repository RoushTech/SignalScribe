using System.Text.Json;
using SignalScribe.Contracts;
using SignalScribe.Enums;
using SignalScribe.Workers.HostApi;

namespace SignalScribe.Workers.Handlers;

/// <summary>
/// Speaker embeddings (milestone 4). Scaffold: completes as a no-op when no embedding model is
/// present so the pipeline stays green end-to-end.
///
/// Segment rows now arrive pre-split at the capture-side boundary markers (see
/// <see cref="SignalScribe.Analysis.ClipSplitter"/>) — one row per over — so each row is already a
/// single-speaker turn candidate and can be embedded whole. The sliding-window pass only needs to
/// look for a speaker change *within* a span (handover with no unkey), emitting an EmbeddingSplit
/// marker; re-running ClipSplitter then subdivides that span on reprocess.
///
/// TODO(milestone 4): ONNX Runtime + an ECAPA-TDNN/WeSpeaker voxceleb export at
/// models/speaker-embedding.onnx — 80-dim log-mel fbank frames (25 ms window / 10 ms hop) →
/// embedding per segment (plus sliding-window pass for within-clip split detection) → POST to a
/// new api/internal/events/embeddings endpoint; session-scoped clustering then labels speakers
/// via extracted callsigns (see plan.md).
/// </summary>
public sealed class EmbeddingHandler(
    IConfiguration config,
    ILogger<EmbeddingHandler> logger) : IJobHandler
{
    private bool _warned;

    public JobType Type => JobType.Embed;

    public Task ExecuteAsync(ClaimedJob job, CancellationToken ct)
    {
        var modelPath = Path.Combine(config.GetValue("Workers:ModelsDirectory", "models")!, "speaker-embedding.onnx");
        if (!File.Exists(modelPath))
        {
            if (!_warned)
            {
                _warned = true;
                logger.LogWarning("No speaker-embedding model at {Path} — embedding jobs are no-ops (milestone 4)", modelPath);
            }

            return Task.CompletedTask;
        }

        throw new NotImplementedException("Embedding inference is milestone 4 — model present but feature extraction not yet implemented.");
    }
}
