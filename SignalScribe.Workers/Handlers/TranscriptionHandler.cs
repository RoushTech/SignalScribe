using System.Text.Json;
using Concentus;
using Concentus.Oggfile;
using SignalScribe.Contracts;
using SignalScribe.Enums;
using SignalScribe.Workers.HostApi;
using SignalScribe.Workers.Settings;
using Whisper.net;

namespace SignalScribe.Workers.Handlers;

/// <summary>
/// Whisper.net transcription: Opus clip → 16 kHz PCM → whisper with the operator's ham-vocabulary
/// prompt → segments posted to the host (which extracts callsigns and enqueues embedding).
/// The factory is cached per model file; a missing model fails the job visibly (dashboard, retry).
/// </summary>
public sealed class TranscriptionHandler(
    IConfiguration config,
    InternalApiClient api,
    WorkerSettingsProvider settings,
    ILogger<TranscriptionHandler> logger) : IJobHandler
{
    private static readonly Lock FactoryLock = new();

    private static WhisperFactory? _factory;

    private static string? _factoryModelPath;

    public JobType Type => JobType.Transcribe;

    public async Task ExecuteAsync(ClaimedJob job, CancellationToken ct)
    {
        var transmissionId = JsonDocument.Parse(job.PayloadJson).RootElement.GetProperty("transmissionId").GetInt64();
        var info = await api.GetTransmissionAsync(transmissionId, ct)
            ?? throw new InvalidOperationException($"Transmission {transmissionId} not found");

        if (info.IsDouble)
        {
            return; // doubles are flagged, not transcribed
        }

        var audioRoot = config.GetValue("Workers:AudioDirectory", "audio")!;
        var clipPath = Path.Combine(audioRoot, info.AudioPath);
        var pcm = DecodeOpus(clipPath);
        var started = System.Diagnostics.Stopwatch.StartNew();
        if (pcm.Length < 16_000 / 2)
        {
            await api.PostTranscriptAsync(new TranscriptIngest(transmissionId, ModelName(), []), ct);
            return;
        }

        // Transcribe each over separately. Handing Whisper the whole recording makes it stop at the
        // first end-of-speech it believes in — on a repeater that is the gap between overs, so
        // everything after it is lost. Capture's boundary markers tell us where the overs are.
        var durationMs = pcm.Length * 1000 / 16_000;
        var threads = SignalScribe.Analysis.WorkerThreads.Resolve(settings.Current.TranscriptionThreads);
        var spans = SignalScribe.Analysis.ClipSplitter.Spans(
            (info.Markers ?? []).Select(m => (m.Type, m.OffsetMs)), durationMs);

        // One segment row per span, not per Whisper sentence: a span is one station's over, which is
        // the unit speaker embedding and clustering need. Whisper's sentence breaks are punctuation,
        // not speaker changes. The embedding pass may later subdivide a span it hears two voices in.
        var segments = new List<TranscriptSegmentIngest>();
        foreach (var (startMs, endMs) in spans)
        {
            var from = Math.Clamp(startMs * 16, 0, pcm.Length);
            var to = Math.Clamp(endMs * 16, from, pcm.Length);
            if (to - from < 16_000 / 4)
            {
                continue;
            }

            var processor = GetFactory().CreateBuilder()
                .WithLanguage("en")
                .WithThreads(threads)
                .WithPrompt(settings.Current.TranscriptionPrompt)
                .Build();

            var spoken = new List<string>();
            await using (processor)
            {
                await foreach (var seg in processor.ProcessAsync(pcm[from..to], ct))
                {
                    var text = seg.Text.Trim();
                    if (text.Length > 0 &&
                        !SignalScribe.Analysis.HallucinationFilter.IsNonSpeechAnnotation(text) &&
                        !SignalScribe.Analysis.HallucinationFilter.IsPromptEcho(text, settings.Current.TranscriptionPrompt))
                    {
                        spoken.Add(text);
                    }
                }
            }

            if (spoken.Count > 0)
            {
                segments.Add(new TranscriptSegmentIngest(startMs, endMs, string.Join(' ', spoken)));
            }
        }

        await api.PostTranscriptAsync(new TranscriptIngest(transmissionId, ModelName(), segments), ct);
        // Realtime factor is the number that matters when sizing the box: >1 means transcription
        // is falling behind the air and the queue will grow without bound.
        var elapsed = started.Elapsed.TotalSeconds;
        logger.LogInformation(
            "Transcribed transmission {Id}: {Count} of {Spans} marker span(s) had speech — "
            + "{Audio:F1}s audio in {Elapsed:F1}s on {Threads} thread(s) ({Rtf:F2}x realtime)",
            transmissionId, segments.Count, spans.Count,
            durationMs / 1000.0, elapsed, threads, elapsed / Math.Max(0.001, durationMs / 1000.0));
    }

    private string ModelName() => $"whisper.net/{settings.Current.WhisperModel}";

    private WhisperFactory GetFactory()
    {
        var modelPath = Path.Combine(config.GetValue("Workers:ModelsDirectory", "models")!, settings.Current.WhisperModel);
        lock (FactoryLock)
        {
            if (_factory is null || _factoryModelPath != modelPath)
            {
                _factory?.Dispose();
                _factory = WhisperFactory.FromPath(modelPath);
                _factoryModelPath = modelPath;
            }

            return _factory;
        }
    }

    private static float[] DecodeOpus(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = OpusCodecFactory.CreateDecoder(16_000, 1);
        var ogg = new OpusOggReadStream(decoder, stream);
        var samples = new List<float>(16_000 * 10);
        while (ogg.HasNextPacket)
        {
            var packet = ogg.DecodeNextPacket();
            if (packet is not null)
            {
                foreach (var s in packet)
                {
                    samples.Add(s / 32768f);
                }
            }
        }

        return [.. samples];
    }
}
