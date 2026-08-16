# SignalScribe — Project Rules

## Project Structure

```
SignalScribe/           Class library — entities, enums, SignalScribeContext, shared contracts
SignalScribe.Api/       ASP.NET Core web host — single DB writer, job-queue API, serves frontend
SignalScribe.Capture/   Capture daemon — SDRPlay/IQ-replay sources, PFB channelizer, demod, Opus clips
SignalScribe.Workers/   Processing workers — transcription, embeddings, net analysis, summaries
SignalScribe.Vue/       Vue 3 / Vite frontend (dev server runs separately)
SignalScribe.Tests/     Tests (incl. IQ-replay DSP regression)
```

## Running the Project

**Web host** (from repo root):
```bash
dotnet run --project SignalScribe.Api
# http://localhost:5020
```

**Capture daemon / workers** (from repo root):
```bash
dotnet run --project SignalScribe.Capture
dotnet run --project SignalScribe.Workers
```

**Frontend** (from `SignalScribe.Vue/`):
```bash
npm run dev
```

**Docker** (from repo root):
```bash
scripts/fetch-sdrplay-api.sh   # once per checkout — populates vendor/sdrplay (proprietary, gitignored)
docker compose up --build
# One image, three services: api, workers, capture. Only `api` mounts the DB
# volume (single-writer, filesystem-enforced). Models go in the `models`
# volume via scripts/download-models.sh — never into the image.
# capture: runs sdrplay_apiService in its entrypoint; USB passes through via the
# /dev/bus/usb bind mount + cgroup rule 189:* (survives unplug/replug).
```

**Known tooling wart:** `dotnet ef` on Linux leaves literal `bin\Debug` directories (backslash in the
name). They break MSBuild resource globs with MSB3552 locally and in Docker builds. `.gitignore` and
`.dockerignore` exclude them; if a build hits MSB3552, run `find . -name '*\\*' -not -path './.git/*' -prune -exec rm -rf {} +`

**EF Core migrations** (from repo root):
```bash
dotnet ef migrations add <Name> \
    --project SignalScribe/SignalScribe.csproj \
    --startup-project SignalScribe.Api/SignalScribe.Api.csproj

dotnet ef migrations remove \
    --project SignalScribe/SignalScribe.csproj \
    --startup-project SignalScribe.Api/SignalScribe.Api.csproj
```

## Stack rules

- **C#/.NET everywhere.** No Python at runtime. ML/inference goes through .NET bindings to native libraries (Whisper.net, LLamaSharp, ONNX Runtime, Concentus). Don't propose other-language components unless there is no viable .NET path — and say why.
- **Fully offline.** No cloud APIs, no Claude/OpenAI, no telemetry. Everything must work with the network unplugged after model download.
- **Never edit an applied migration.** Once a migration is in a database's `__EFMigrationsHistory`, changing its file — including its seed `HasData`, its Designer, or the model snapshot — cannot be picked up by that database. The only way to converge is to drop the data and re-migrate, so every such edit costs the operator their recordings. Model or seed changes get a **new** migration, always. The sole exception is squashing, which is a deliberate reset the operator has asked for; do the whole squash in one pass, and once it is applied it is frozen like any other.
- **EF Core, never bare SQLite.** All schema changes go through EF migrations. The FTS5 virtual table is created with `migrationBuilder.Sql()` inside a normal migration (the bundled native provider has FTS5 compiled in) and queried via an FTS-mapped keyless entity + `FromSql` interpolated `MATCH`, composed with LINQ. No `SqliteConnection` used directly outside the EF provider (in-memory test connections excepted — see Testing).

## Architecture invariants

- **Single writer.** Only the web host touches the database. Capture daemon and workers talk to it over HTTP. Workers claim jobs with lease semantics; completions are idempotent.
- **Capture never blocks.** The capture daemon writes audio to disk itself and spools metadata events to a local append-only journal when the host is unreachable, replaying on reconnect. Audio is never streamed over HTTP — metadata carries file paths.
- **Channels auto-create on voice, not on squelch.** A transmission on an unknown frequency must pass the capture-side voice-likeness gate (syllabic 2–8 Hz envelope modulation + speech-band energy + min duration) before it is posted and the channel row springs into existence; non-voice activity on unknown frequencies is dropped. Known channels record everything their squelch opens — but that trust is revocable: a channel with `ChannelVoiceAudit.MinResolvedRecordings` settled recordings and no speech in any of them is auto-disabled (`Channel.AutoDisabledReason`), dropping out of the daemon's known set so its traffic must pass the voice gate again. This is what stops a data frequency (APRS, packet, a stuck carrier) recording every burst forever; the first real transcript re-enables it. The worker-side VAD remains the authoritative voice check before transcription.
- **One segment row per over.** Transcription splits a clip at the capture-side boundary markers (`ClipSplitter`) and runs the model on each span independently — handing a whole multi-over clip to Whisper loses everything after the first over, because it treats the inter-over gap as end-of-speech. A `Segment` is therefore one station's over: the unit speaker embedding and clustering need. Never split segments on the model's sentence breaks; those are punctuation, not speaker changes.
- **A double is a heterodyne, not any steady tone.** `IsDouble` and the `HeterodyneDouble` marker come from a sustained tone **inside** 300–2700 Hz — two carriers on one channel beat into an audible whistle there. Hum, leaked CTCSS and buzz sit below the band; flagging those as doubles suppresses transcription on perfectly readable audio (observed on 146.925). Out-of-band tones produce no marker at all: a hum is not a courtesy tone and must never split a clip into overs.
- **A tone under the voice is not a tone instead of the voice.** Real transmissions carry hum, leaked CTCSS, link tones and buzz, and the tone is audible in the *gaps between words* — so every pause reads as a pure tone. A sustained tone therefore only disqualifies speech when it lies **inside** the 300–2700 Hz voice band, where it could be mistaken for speech; one outside it is something the speech sits on top of. A dominant narrowband peak outside that band is also subtracted before judging speech-band dominance, and the envelope that drives the syllable test is high-passed at 300 Hz — measured broadband, a steady tone raises the floor and flattens the very variance the test looks for (observed on 147.180: 13 s of a man talking scored 0.15 speech ratio, 864 ms voiced, 0.10 modulation depth).
- **Squelch gates recording, not segmentation.** Transmission boundaries are separate marker events (repeater squelch crash, courtesy tone, RF edges, discriminator DC-offset jumps), refined downstream by within-clip speaker-embedding splits. Never assume one squelch-open == one speaker (quick-keying; repeater tails keep the carrier up).
- **ADC overload means go quiet, not go deaf.** The SDRplay API reports front-end overload as a start/stop pair (`sdrplay_api_PowerOverloadChange`, with the change type in the first field of the event-params union). While overloaded the ADC is clipping and every bin carries compression products instead of signal, so `ChannelBank.Overloaded` holds every gate shut for the duration — a transmitter close enough to desense the receiver (the operator's own beacon) otherwise opens the whole band at once. Gates already open keep their clip; they were real before the overload hit. On the way out, re-reference every *closed* channel's floor in one block — never an open one, which would cut a transmission off mid-over. Levels alone cannot tell you this; the hardware can.
- **The overload report lags the overload.** `sdrplay_api_PowerOverloadChange` arrives after the ADC is already clipping, so gates opened in the half-second before it are part of the same event and are disowned retroactively (discarded, never posted). Gates stay shut for a guard period after the *clear* too — the front end does not recover instantly, and a gate opened into that transient latched for 49 s on air. Floor re-seeding after an overload may only pull floors **down** (`Math.Min`): adopting the current level outright blinds any channel that happens to be transmitting when the guard expires.
- **A gate that never closes is not a transmission.** The max-open safety valve measures from the hop the *gate* opened (`GateOpenedHop`), never from the clip start — clip rollover starts a new `ActiveTransmission`, so a valve keyed on clip start resets every 90 s and never fires. On force-close, re-seed that channel's noise floor from its current level: the floor is frozen while a gate is open, so a latched channel is by definition sitting above a stale floor and will re-latch on the next block otherwise. Both are covered by `PersistentCarrierIsForceClosedOnceAndDoesNotRelatch`.
- **Squelch decides in *relative* space.** Gate thresholds compare each channel against the band median, never absolute dBFS: the RSP's AGC steps every channel's gain at once, and absolute-level squelch latches every gate open when it does. Audio level is likewise deviation-referenced (FM is constant-envelope — RF AGC has no effect on demodulated loudness) with a soft limiter so over-deviating stations compress instead of clipping.
- **Squelch tones are read, then filtered.** CTCSS (67–254 Hz) and DCS (a 134.4 bps sub-audible bitstream) sit below the voice, and 750 µs de-emphasis is flat below its 212 Hz corner while rolling voice off above it — so untouched they arrive *louder* than the speech (measured: 55–76% of recorded energy). `SubaudibleDetector` taps the discriminator **before** the 300 Hz audio high-pass, because that filter exists to discard exactly what it reads; anything added to the audio chain must stay downstream of the tap. Detection costs 0.113% of one core per open gate (3.6% with all 32 open), so it runs on **every** transmission unconditionally — never gated on whether a channel already has a tone configured, because the interesting cases are the mismatches: a transmission with no tone on a channel that normally has one is someone with their tone switched off, and a different tone is a different system sharing the frequency. The measured tone is persisted per transmission and learned onto the channel (`ChannelLearnedState.CtcssToneHz`) — two repeaters commonly share an output frequency and the tone is what tells them apart. Never round a measured frequency onto the standard tone table without a confidence check: a mains hum is not the nearest CTCSS tone. A tone is accepted only if it beats its runner-up, beats guard bins 1.2 Hz either side of itself, **and** holds a real share of sub-audible energy — all three, because each alone admits something (a DCS bitstream is peaky, plain speech has margin). CTCSS and DCS are alternative systems, never both: a tone beating against the 134.4 bps bit clock produces a repeating, Golay-valid phantom code, which is how 146.640 (CTCSS 146.2) reported DCS 073.
- **Model pre-emphasis when testing sub-audible DSP.** Transmitters pre-emphasise voice +6 dB/octave but inject CTCSS/DCS *after* that stage, so at the discriminator the voice's low end is ~20 dB below its raw level. A fixture that omits this makes the sub-audible band look far dirtier than air: tuned against it, 21 of 83 DCS codes decoded as the *wrong* valid code. Equally, a fixture whose speech has nothing below 300 Hz makes every threshold look safe — the pitch fundamental sits inside the CTCSS range and wanders, and that is the real thing a tone must be told apart from.
- **CTCSS 179.9 Hz is indistinguishable from 60 Hz mains' third harmonic** (0.1 Hz apart) inside one over. Do not write tests that demand otherwise.
- **Audio is ground truth.** Clips are Opus/OGG at 16 kHz, 32 kbps VBR voice mode. Every transcript/embedding row records the model name + version that produced it so results can be reprocessed when models improve.
- **Deterministic before LLM.** Facts (check-in rosters, net control, schedules, durations) come from code over the database. The local LLM only writes narrative prose from those facts.

## .NET / EF Core

- EF Core models live in the `SignalScribe.Data.Models` namespace under `SignalScribe/Data/Models/`
- `SignalScribeContext` lives in the `SignalScribe.Data` namespace at `SignalScribe/Data/`
- Enums live in the `SignalScribe.Enums` namespace under `SignalScribe/Enums/`
- Each entity implements `IEntityTypeConfiguration<T>` directly on itself (not a nested class); the `Configure` method holds all fluent API config for that model
- Do not add fluent API calls directly in `OnModelCreating`
- The `DbContext` discovers all configs by scanning the assembly: `modelBuilder.ApplyConfigurationsFromAssembly(typeof(SignalScribeContext).Assembly)`
- JSON columns (e.g. per-channel learned state, model metadata) are stored as `string` / `string?` in the database; the entity exposes a typed, deserialized property and handles serialisation/deserialisation itself — callers always work with the typed value, never the raw JSON string
- All `DateTime` properties are UTC (capture-side timestamps derive from the SDR sample counter anchored to NTP-synced wall clock once per stream — never per-event wall clock reads). Net schedules are stored in UTC too (day-of-week + time-of-day columns); **the browser converts to/from local time for display and entry** — no timezone data server-side
- Service status streams over SignalR: capture/workers are SignalR *clients* pushing `ServiceStatusUpdate`s to the `/hubs/status` hub; the hub keeps latest-per-service **in memory only** (never the database — daemons re-push on reconnect) and broadcasts to browser clients
- **Operator settings live in the DB, not appsettings**: single-row entities (`CaptureSettings`, `WorkerSettings`, Id=1, seeded by migration), edited via `api/v0/settings`, fetched by daemons via `api/internal/settings`, change-notified via `settingsChanged` on the status hub (daemons re-fetch and apply live). appsettings/env carries only deployment config (paths, host URL, source selection) and unreachable-host fallbacks. Capture distinguishes disruptive settings (frequency/rate/spacing ⇒ pipeline rebuild, close clips gracefully) from live-apply (gain/AGC/squelch)
- Use controllers for all API endpoints — do not use minimal API (`app.MapGet` / `app.MapPost` etc.)
- All UI-facing controllers use the route prefix `api/v0/` — the `v0` prefix signals this is an unsupported UI-only API surface; the sole exception is `HealthController` which stays at `/health`. Internal daemon/worker endpoints (event ingest, job queue) use `api/internal/`
- Controller request/response models (DTOs) live in `SignalScribe.Api/Controllers/Models/` under the namespace `SignalScribe.Api.Controllers.Models` — keep models in that folder, not in a separate `Contracts/` directory
- `appsettings.local.json` is git-ignored and always loaded as an optional override file — use it for local paths or overrides
- In `Program.cs`, extract `var services = builder.Services` and `var config = builder.Configuration` as the first two lines after `WebApplication.CreateBuilder`, then use `services.` and `config.` exclusively for all subsequent registrations and configuration access — `builder.Services` and `builder.Configuration` must never appear again after the extraction
- Chain service registrations in `Program.cs` into a single `services` chain. When a method enters a sub-builder (`AddControllers`, `AddSignalR`, `AddHttpClient`, etc.), chain that sub-builder's own methods then fold back to `IServiceCollection` via `.Services` to continue the main chain — indent sub-builder methods one extra level so the fold-back is visually obvious. Only break into a separate `services.` call if the sub-builder genuinely has no `.Services` escape hatch. Chain all `app.Use*` middleware together since they all return `IApplicationBuilder`; `app.Map*` calls each stand on their own line because they return different endpoint-builder types with no path back to `IApplicationBuilder`

## DSP conventions

- Hot paths use `Span<T>` / `Vector<T>` / `System.Runtime.Intrinsics`; no allocations inside the sample loop
- The capture daemon must support an IQ-file replay source interchangeable with the RSP1 source; DSP changes need replay-based regression coverage
- Per-channel learned state (noise floor, courtesy-tone signature, repeater/simplex classification) is persisted via the host API, not kept only in memory
- Handle the SDRPlay service wedging: detect a stalled sample counter, tear down, re-init. Log ADC overload counts

## Vue / Frontend

- All Axios HTTP calls are made through TypeScript classes located in `src/api` — no Axios requests outside of this directory
- All API files import the shared Axios instance from `src/api/axios.ts` — do not call `axios.create` in individual API files; add interceptors or shared config in `axios.ts`
- CORS is never needed and must never be added — in development the Vite dev server proxies `/api`, `/swagger`, and `/hubs` to `http://localhost:5020`; in production the API serves the built frontend as static files, so all requests are same-origin
- State that is only used within a single component or class stays local; only reach for Pinia when sharing state across components or classes
- Use Vuetify for all UI components
- Path alias `@` resolves to `src/`
- Formatting is handled by `oxfmt`; linting by `oxlint` then `eslint` — run `npm run lint` before committing

## Testing

- Every new DSP path (channel type handled, segmentation marker, demod branch) must have a replay-based regression test in `SignalScribe.Tests` driven by a checked-in or generated IQ fixture
- Every new parsing/analysis path (phonetic→callsign normalization case, net-detection heuristic, marker interpretation) must have a corresponding unit test
- Logic that can be extracted as pure functions (segmentation decisions, callsign normalization, recurrence mining) belongs in static helper classes so it can be tested without constructing the full background service
- Use an in-memory SQLite database (via `SqliteConnection("DataSource=:memory:")` + `DbContextOptionsBuilder.UseSqlite`) for tests that exercise DB-level logic — do not use the EF Core in-memory provider, as it does not enforce SQL translation constraints
- Run `dotnet test SignalScribe.Tests/SignalScribe.Tests.csproj` before committing; all tests must pass
