# SignalScribe — Project Rules

## Stack rules

- **C#/.NET everywhere.** No Python at runtime. ML/inference goes through .NET bindings to native libraries (Whisper.net, LLamaSharp, ONNX Runtime, Concentus). Don't propose other-language components unless there is no viable .NET path — and say why.
- **Fully offline.** No cloud APIs, no Claude/OpenAI, no telemetry. Everything must work with the network unplugged after model download.
- **EF Core, never bare SQLite.** All schema changes go through EF migrations. The FTS5 virtual table is created with `migrationBuilder.Sql()` inside a normal migration (the bundled native provider has FTS5 compiled in) and queried via an FTS-mapped keyless entity + `FromSql` interpolated `MATCH`, composed with LINQ. No `SqliteConnection` used directly outside the EF provider.

## Architecture invariants

- **Single writer.** Only the web host touches the database. Capture daemon and workers talk to it over HTTP. Workers claim jobs with lease semantics; completions are idempotent.
- **Capture never blocks.** The capture daemon writes audio to disk itself and spools metadata events to a local append-only journal when the host is unreachable, replaying on reconnect. Audio is never streamed over HTTP — metadata carries file paths.
- **Squelch gates recording, not segmentation.** Transmission boundaries are separate marker events (repeater squelch crash, courtesy tone, RF edges, discriminator DC-offset jumps), refined downstream by within-clip speaker-embedding splits. Never assume one squelch-open == one speaker (quick-keying; repeater tails keep the carrier up).
- **Audio is ground truth.** Clips are Opus/OGG at 16 kHz, 32 kbps VBR voice mode. Every transcript/embedding row records the model name + version that produced it so results can be reprocessed when models improve.
- **Deterministic before LLM.** Facts (check-in rosters, net control, schedules, durations) come from code over the database. The local LLM only writes narrative prose from those facts.

## Conventions

- Timestamps: UTC everywhere, derived from the SDR sample counter anchored to NTP-synced wall clock once per stream — never per-event wall clock reads.
- DSP hot paths use `Span<T>` / `Vector<T>` / `System.Runtime.Intrinsics`; no allocations inside the sample loop.
- The capture daemon must support an IQ-file replay source interchangeable with the RSP1 source; DSP changes need replay-based regression coverage.
- Per-channel learned state (noise floor, courtesy-tone signature, repeater/simplex classification) is persisted via the host API, not kept only in memory.
- Handle the SDRPlay service wedging: detect a stalled sample counter, tear down, re-init. Log ADC overload counts.
