# SignalScribe

Offline 2m-band monitor. Captures the entire amateur 2m band (144–148 MHz) from an SDRPlay RSP1, detects and demodulates live NBFM transmissions across all channels in parallel, transcribes them locally, archives everything searchable, identifies speakers, and detects and summarizes nets — with **no cloud dependencies**.

See [plan.md](plan.md) for the roadmap and design rationale, and [CLAUDE.md](CLAUDE.md) for project rules.

## Architecture

```
RSP1 ──IQ (~6 MSPS)──► Capture Daemon (C#)
                        │  polyphase filterbank channelizer (~300 ch)
                        │  per-channel adaptive squelch + NBFM demod
                        │  segmentation markers (squelch crash, courtesy
                        │  tone, RF edges, discriminator DC-offset jumps)
                        │
                        ├──► Opus (OGG) clips on disk
                        └──► metadata/events ──► Web Host (ASP.NET Core)
                                                 │  EF Core → SQLite (WAL)
                                                 │  ** single writer **
                                                 │  job queue API
                                                 │  Vue 3 dashboard
                                                 ▼
              Processing Workers (C#) ◄── claim jobs via host API
              │  transcription   — Whisper.net (whisper.cpp)
              │  speaker embed   — ECAPA-TDNN via ONNX Runtime
              │  net analysis    — deterministic heuristics
              │  net summaries   — LLamaSharp (llama.cpp, 8B Q4)
              └──► results posted back to host API
```

## Hardware requirements

| Component | Minimum | Recommended | Notes |
|---|---|---|---|
| SDR | SDRPlay RSP1 | RSP1 + 2m bandpass filter | RSP1 has minimal preselection; FM broadcast/pagers can overload it. Run ~6 MSPS (2m band is only 4 MHz wide). |
| CPU | 6 cores with AVX2 | 8+ cores (Zen 3+/12th-gen Intel+) | LLM inference is **memory-bandwidth bound** — dual-channel RAM matters more than core count; >8 threads gives diminishing returns. CPUs without AVX (e.g. QEMU default vCPUs — use `-cpu host`!) work via bundled no-AVX runtimes but inference is several times slower. |
| RAM | 16 GB (tight) | 32 GB | See per-model budget below. |
| Disk | 50 GB | 100 GB+ SSD | Audio recorded directly as 16 kHz Opus-in-OGG, 32 kbps VBR voice mode (~14 MB per hour of recorded audio). Optional raw WAV/IQ capture behind a debug flag for DSP development. |
| USB | USB 2.0 port | — | 6 MSPS fits USB 2.0. |
| OS | Linux, .NET 8+ | — | Requires SDRPlay API service (proprietary, free download). |

### Compute budget by workload

| Workload | Model | RAM | CPU cost | Latency expectation |
|---|---|---|---|---|
| Channelizer + demod | — (custom PFB) | < 1 GB | 1–2 cores, continuous | Real-time |
| Transcription | Whisper `small.en` (q5) | ~1 GB | Bursty | Faster than real-time on 8 cores |
| Transcription (higher accuracy) | Whisper `medium.en` (q5) | ~2.5 GB | Bursty | ~Real-time; backlog drains overnight |
| Speaker embeddings | ECAPA-TDNN (ONNX) | ~100 MB | Negligible | Per-clip, milliseconds |
| Net summaries | Qwen 2.5 / Llama 3.x **8B Q4_K_M** | ~5 GB model, 6–8 GB process | Batch, after net closes | 5–15 tok/s generation → **2–4 min per net summary** |
| Net summaries (low-power box) | Qwen 2.5 **3B Q4** | ~2.5 GB | Batch | Faster; quality sufficient since facts come from the deterministic layer |

Everything above runs concurrently on one 8-core / 32 GB machine. All inference is local; nothing requires network access after model download.

## Software stack

| Layer | Tech |
|---|---|
| DSP / capture | C# (.NET 8), `sdrplay_api` P/Invoke, `System.Runtime.Intrinsics` |
| Audio encoding | Opus in OGG via [Concentus](https://github.com/lostromb/concentus) (managed Opus, native 16 kHz speech mode) |
| Transcription | [Whisper.net](https://github.com/sandrohanea/whisper.net) (whisper.cpp bindings) |
| Speaker ID | ONNX Runtime + ECAPA-TDNN embeddings, clustered per session, labeled via extracted callsigns |
| Summaries | [LLamaSharp](https://github.com/SciSharp/LLamaSharp) (llama.cpp bindings) |
| Storage | SQLite (WAL mode) via **EF Core**; FTS5 transcript index (see CLAUDE.md for the pattern) |
| API / UI | ASP.NET Core (controllers, `api/v0/`) + Vue 3 + Vuetify |

## Legal note

US amateur transmissions carry no expectation of privacy (Part 97; encryption prohibited), so recording, transcribing, and publishing this traffic is permitted.
