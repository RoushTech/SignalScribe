using System.Collections.Concurrent;
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
/// Whisper.net transcription: Opus clips → 16 kHz PCM → whisper with the operator's ham-vocabulary
/// prompt → segments posted to the host (which extracts callsigns and enqueues embedding).
///
/// Batched: Whisper pads every run to a 30-second mel window, so cost is per run, not per second —
/// measured, a 1-second kerchunk and a 20-second over both took ~6.3 s. The whole claim is
/// therefore decoded up front and every clip's spans packed together (<see cref="SignalScribe.Analysis.SpeechPacker"/>),
/// then the packed windows run concurrently in lanes. The factory is cached per model file; a
/// missing model fails the job visibly (dashboard, retry).
/// </summary>
public sealed class TranscriptionHandler(
    IConfiguration config,
    InternalApiClient api,
    WorkerSettingsProvider settings,
    ILogger<TranscriptionHandler> logger) : IBatchJobHandler
{
    private static readonly Lock FactoryLock = new();

    private static WhisperFactory? _factory;

    private static string? _factoryModelPath;

    public JobType Type => JobType.Transcribe;

    public async Task ExecuteAsync(ClaimedJob job, CancellationToken ct)
    {
        var outcome = await ExecuteBatchAsync([job], ct);
        if (outcome.TryGetValue(job.Id, out var error) && error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>One clip's work: where its spans sit in its PCM, and what was heard in each.</summary>
    private sealed record Clip(
        long JobId,
        long TransmissionId,
        long FrequencyHz,
        float[] Pcm,
        int DurationMs,
        int SpanCount,
        List<(int StartMs, int EndMs, int From, int To)> Live)
    {
        public List<string>[] Spoken { get; } = [.. Live.Select(_ => new List<string>())];
    }

    public async Task<IReadOnlyDictionary<long, string?>> ExecuteBatchAsync(IReadOnlyList<ClaimedJob> jobs, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var outcomes = new ConcurrentDictionary<long, string?>();
        var clips = new List<Clip>();

        // Phase 1 — fetch and decode everything. Cheap next to inference, and failures here are
        // per-clip: a deleted transmission or unreadable file fails its own job and nothing else.
        foreach (var job in jobs)
        {
            try
            {
                var clip = await PrepareAsync(job, ct);
                if (clip is not null)
                {
                    clips.Add(clip);
                }
                else
                {
                    outcomes[job.Id] = null; // double, or empty clip already answered with an empty transcript
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcomes[job.Id] = ex.Message;
            }
        }

        // Same channel's overs side by side: whisper conditions on the text before each segment
        // within a run, and one conversation is far better context than four unrelated frequencies
        // interleaved by claim order.
        clips.Sort((a, b) => (a.FrequencyHz, a.TransmissionId).CompareTo((b.FrequencyHz, b.TransmissionId)));

        // Phase 2 — pack every span from every clip into as few whisper windows as possible.
        var flat = new List<(int ClipIndex, int SpanIndex)>();
        var lengths = new List<int>();
        for (var c = 0; c < clips.Count; c++)
        {
            for (var s = 0; s < clips[c].Live.Count; s++)
            {
                flat.Add((c, s));
                lengths.Add(clips[c].Live[s].EndMs - clips[c].Live[s].StartMs);
            }
        }

        var windows = SignalScribe.Analysis.SpeechPacker.Plan(lengths);

        // Phase 3 — run the windows across lanes. Whisper's scaling is past its knee by three
        // threads (the fixed window dominates), so narrow-and-parallel beats one wide run; one core
        // stays reserved for capture either way (WorkerThreads).
        var (lanes, threads) = SignalScribe.Analysis.WorkerThreads.PlanLanes(settings.Current.TranscriptionThreads);
        await Parallel.ForEachAsync(
            windows,
            new ParallelOptions { MaxDegreeOfParallelism = lanes, CancellationToken = ct },
            async (window, token) =>
            {
                try
                {
                    await TranscribeWindowAsync(window, flat, clips, threads, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The window is lost, so every clip with a span in it is lost — but only those.
                    foreach (var p in window.Placements)
                    {
                        outcomes[clips[flat[p.SpanIndex].ClipIndex].JobId] = $"whisper run failed: {ex.Message}";
                    }
                }
            });

        // Phase 4 — one transcript post per transmission, exactly as before batching.
        foreach (var clip in clips)
        {
            if (outcomes.TryGetValue(clip.JobId, out var error) && error is not null)
            {
                continue;
            }

            try
            {
                var segments = new List<TranscriptSegmentIngest>();
                for (var s = 0; s < clip.Live.Count; s++)
                {
                    if (clip.Spoken[s].Count > 0)
                    {
                        segments.Add(new TranscriptSegmentIngest(clip.Live[s].StartMs, clip.Live[s].EndMs, string.Join(' ', clip.Spoken[s])));
                    }
                }

                await api.PostTranscriptAsync(new TranscriptIngest(clip.TransmissionId, ModelName(), segments), ct);
                outcomes[clip.JobId] = null;
                logger.LogInformation(
                    "Transcribed transmission {Id}: {Count} of {Spans} marker span(s) had speech ({Audio:F1}s audio)",
                    clip.TransmissionId, segments.Count, clip.SpanCount, clip.DurationMs / 1000.0);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcomes[clip.JobId] = ex.Message;
            }
        }

        if (clips.Count > 0)
        {
            // Realtime factor is the number that matters when sizing the box: >1 means transcription
            // is falling behind the air. Runs-vs-spans is what the packing is buying.
            var audio = clips.Sum(c => c.DurationMs) / 1000.0;
            var elapsed = started.Elapsed.TotalSeconds;
            logger.LogInformation(
                "Batch of {Jobs} job(s): {Spans} span(s) in {Runs} whisper run(s) — "
                + "{Audio:F1}s audio in {Elapsed:F1}s, {Lanes} lane(s) × {Threads} thread(s) ({Rtf:F2}x realtime)",
                jobs.Count, flat.Count, windows.Count, audio, elapsed, lanes, threads, elapsed / Math.Max(0.001, audio));
        }

        return outcomes;
    }

    /// <summary>Fetch, decode and span one job's clip. Null means the job is already fully answered.</summary>
    private async Task<Clip?> PrepareAsync(ClaimedJob job, CancellationToken ct)
    {
        var transmissionId = JsonDocument.Parse(job.PayloadJson).RootElement.GetProperty("transmissionId").GetInt64();
        var info = await api.GetTransmissionAsync(transmissionId, ct)
            ?? throw new InvalidOperationException($"Transmission {transmissionId} not found");

        if (info.IsDouble)
        {
            return null; // doubles are flagged, not transcribed
        }

        var audioRoot = config.GetValue("Workers:AudioDirectory", "audio")!;
        var pcm = DecodeOpus(Path.Combine(audioRoot, info.AudioPath));
        if (pcm.Length < 16_000 / 2)
        {
            await api.PostTranscriptAsync(new TranscriptIngest(transmissionId, ModelName(), []), ct);
            return null;
        }

        // Split at capture's boundary markers. Handing Whisper a whole recording makes it stop at
        // the first end-of-speech it believes in — on a repeater that is the gap between overs, so
        // everything after it is lost.
        var durationMs = pcm.Length * 1000 / 16_000;
        var spans = SignalScribe.Analysis.ClipSplitter.Spans(
            (info.Markers ?? []).Select(m => (m.Type, m.OffsetMs)), durationMs);

        // No second voice gate here. Capture already measured speech-band dominance over this exact
        // audio and EventsController refuses to enqueue below 300 ms of it, so everything that
        // reaches this method has passed that test. Measured against 687 spans Whisper had produced
        // text for and 55 clips it had found nothing in, a worker-side repeat of the same test
        // skipped 1 of the 55 while already costing real transcripts — it agrees with the gate
        // upstream because it *is* the gate upstream. Telling weak or mumbled speech from good
        // speech needs a learned model, not another spectral threshold.
        var live = new List<(int StartMs, int EndMs, int From, int To)>();
        foreach (var (startMs, endMs) in spans)
        {
            var from = Math.Clamp(startMs * 16, 0, pcm.Length);
            var to = Math.Clamp(endMs * 16, from, pcm.Length);
            if (to - from >= 16_000 / 4)
            {
                live.Add((startMs, endMs, from, to));
            }
        }

        return new Clip(job.Id, transmissionId, info.FrequencyHz, pcm, durationMs, spans.Count, live);
    }

    /// <summary>One packed window through Whisper, with each segment attributed back to its span.</summary>
    private async Task TranscribeWindowAsync(
        SignalScribe.Analysis.SpeechPacker.Window window,
        List<(int ClipIndex, int SpanIndex)> flat,
        List<Clip> clips,
        int threads,
        CancellationToken ct)
    {
        var buffer = new float[window.TotalMs * 16];
        foreach (var p in window.Placements)
        {
            var (clipIndex, spanIndex) = flat[p.SpanIndex];
            var (_, _, from, to) = clips[clipIndex].Live[spanIndex];
            clips[clipIndex].Pcm.AsSpan(from..to).CopyTo(buffer.AsSpan(p.OffsetMs * 16, to - from));
        }

        // TemperatureInc(0) disables whisper's temperature-fallback ladder: by default a decode
        // whose logprob or entropy looks bad is retried at up to five rising temperatures, and on
        // squelch noise it *always* looks bad — measured, 1.2 s of noise cost 11.3 s of compute.
        // The retries are also where sampling gets hot enough to hallucinate, which is what
        // HallucinationFilter exists to clean up. One deterministic pass: the clips whisper was
        // rescuing with fallback are the ones we want it to shrug at.
        var processor = GetFactory().CreateBuilder()
            .WithLanguage("en")
            .WithThreads(threads)
            .WithTemperatureInc(0f)
            .WithPrompt(settings.Current.TranscriptionPrompt)
            .Build();

        await using (processor)
        {
            await foreach (var seg in processor.ProcessAsync(buffer, ct))
            {
                var text = seg.Text.Trim();
                if (text.Length == 0 ||
                    SignalScribe.Analysis.HallucinationFilter.IsNonSpeechAnnotation(text) ||
                    SignalScribe.Analysis.HallucinationFilter.IsPromptEcho(text, settings.Current.TranscriptionPrompt))
                {
                    continue;
                }

                // Attribute by the midpoint rather than the start: Whisper likes to open a segment
                // slightly early, which on a packed window means inside the previous span's tail.
                var midMs = (int)((seg.Start + seg.End).TotalMilliseconds / 2);
                var (clipIndex, spanIndex) = flat[SignalScribe.Analysis.SpeechPacker.SpanAt(window, midMs)];
                clips[clipIndex].Spoken[spanIndex].Add(text);
            }
        }
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
