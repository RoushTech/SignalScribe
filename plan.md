# SignalScribe — Plan & Rationale

What we're building, in what order, and why each decision went the way it did.

## Goal

Park an RSP1 on the 2m band and turn everything that happens there into a searchable, documented archive: live transmissions → transcripts → speakers → nets → summaries. Runs unattended on one machine, fully offline.

## Decisions and why

| Decision | Why |
|---|---|
| **Custom C# DSP** instead of rtl_airband / ka9q-radio | DireControl proved C# DSP is fast to write and performs well (`Vector<T>`/intrinsics). Full control over scanning, squelch, and segmentation markers; single-stack coherence. A polyphase filterbank produces *all* ~300 channels simultaneously in one pass, so parallel QSOs cost nothing by construction. |
| **~6 MSPS, not the RSP1's full 10 MHz** | The US 2m band is only 4 MHz wide. Lower rate = better effective bit depth, less USB/CPU load. Center frequency parked *between* channels so the zero-IF DC spike doesn't land on anyone. |
| **Opus/OGG direct capture, no WAV** | ~14 MB/hr vs 115 MB/hr. Opus has a native 16 kHz speech mode matching Whisper's input; 32 kbps VBR is transparent enough for ASR and speaker embeddings. The DC-offset transmitter fingerprint is computed in the DSP layer before encoding, so lossy storage doesn't affect it. Raw WAV/IQ stays available behind a debug flag. |
| **Layered segmentation, squelch demoted** | Quick-keying merges speakers, and on repeater outputs the carrier never drops between transmissions (the repeater tail keeps transmitting). So: audio-domain markers on repeaters (squelch crash, courtesy tone), RF edges + discriminator DC-offset jumps on simplex, and within-clip embedding-similarity splits as the backstop. Doubles are detected via heterodyne tone and flagged, not transcribed. |
| **Whisper (local) + prompt seeding + phonetic post-processing** | Fully offline. No good public ham fine-tune exists; `initial_prompt` ham vocabulary + deterministic phonetic→callsign normalization ("kilo delta nine…" → `KD9…`) gets most of the accuracy. Fine-tune later from corrected transcripts if needed. |
| **Speaker ID = clustering across transmissions, not in-audio diarization** | PTT already segments speakers — each transmission is (almost always) one speaker. Compute one ECAPA embedding per clip, cluster within a session, then label clusters via extracted callsigns (hams must ID every 10 min). Far more robust than pyannote-style diarization on FM audio. |
| **Single writer via web host API** | SQLite has one-writer semantics; funneling all writes through the host makes that structural instead of hoped-for. Workers get a job-queue API with leases; the capture daemon spools locally so it never blocks on the host. |
| **EF Core + FTS5** | FTS5 gives millisecond transcript search at any corpus size, prefix matching for partial callsigns (`kd9*`), BM25 ranking, and `snippet()` for the UI — vs. ever-slower `LIKE` scans. EF Core can't author virtual tables or express `MATCH` in LINQ, but both have first-class escape hatches (`migrationBuilder.Sql()`, `FromSql`), so everything stays in the EF migration/query pipeline. |
| **Status over SignalR, in-memory only** | Capture/workers hold a socket open to the API (`/hubs/status`) and stream status; the hub relays live to browsers. No DB table — status is ephemeral, daemons re-push on reconnect, and the Jobs table already answers queue-depth questions deterministically. |
| **Net windows in UTC, browser converts** | Schedule columns (day-of-week, start time, duration) live on `Net` in UTC like every other timestamp; the UI converts local↔UTC at display/entry. Declared windows drive deterministic session classification (overlap ⇒ `IsNet`/`NetId`). Accepted trade-off: a UTC-fixed weekly time shifts one hour local across DST changes. |
| **Deviation-referenced audio + soft limiter** | FM audio amplitude tracks *deviation*, not RF level, so the RSP's AGC cannot control it. Full scale is referenced to ±5 kHz (standard amateur NBFM; operator-tunable for narrowband) and a soft-knee limiter bounds the output, so a loud/over-deviating station compresses smoothly instead of hard-clipping at the PCM conversion. |
| **Relative-level squelch** | Gates compare each channel to the band median rather than absolute dBFS. A band-wide AGC gain step otherwise lifts every channel above its stale floor at once — observed on air as 30+ gates latching open and recording 100-second noise clips. A max-open safety valve and a signal-present (not hang-inflated) duration test back it up. |
| **Voice-gated channel auto-creation** | Unknown frequencies only become channels when a transmission *sounds like voice* — a cheap capture-side gate on the demodulated audio (syllabic envelope modulation at ~2–8 Hz + speech-band energy ratio + minimum duration), confirmed later by the worker-side VAD before transcription. Non-voice activity on unknown frequencies (birdies, spurs, AFSK/packet, carrier-only kerchunks) is discarded, not posted. Once a channel exists (auto or manual), everything its squelch opens is recorded — the voice gate governs channel *creation*, not capture on known channels. |
| **Deterministic net facts, LLM for prose only** | Check-ins, net control, schedule, and durations are database queries + recurrence mining — reliable and testable. The local 8B-Q4 model (LLamaSharp) only writes the narrative summary as a post-net batch job (2–4 min/net on CPU is irrelevant there). No cloud LLM: hard offline requirement. |

## Deployment

**One image, three containers.** A single Dockerfile builds all three binaries (plus the Vue app, served by the Api as static files); docker-compose runs them as separate services (`api`, `workers`, `capture`) with different commands. Rationale:

- Container-per-process is what makes Docker's supervision usable: per-service `restart`, `mem_limit`, `cpus`. A llama.cpp segfault restarts `workers`, not the dashboard.
- **Only `api` mounts the database volume** — the single-writer invariant becomes filesystem-enforced.
- Audio flows through a shared `audio` volume (capture writes, workers read, api reads for playback). Models live in a `models` volume, downloaded by script — never baked into the image.
- The `capture` container needs USB passthrough (`/dev/bus/usb` + host udev rules) and runs the proprietary `sdrplay_api` service in its entrypoint. **Fallback if that fights back:** run capture on the host — it only talks HTTP + the audio directory, so nothing else changes.

## Milestones

> **Done so far — the full pipeline:** solution scaffold, schema + FTS5 migrations, all API surfaces, SignalR status/settings/spectrum streaming, management UI, sdrplay_api P/Invoke (enumeration + streaming, verified live on an RSP2), dashboard waterfall — **plus milestone 1's DSP core**: 2×-oversampled polyphase channelizer (512 ch @ 6.4 MSPS, vectorized fold, <1 KB/pass steady-state allocation, ~70% of one core live), per-channel adaptive squelch (floor seeding, 2-block click immunity, local-max dedup, hang), NBFM demod (DC-offset fingerprinting, de-emphasis, 16 kHz resample), audio analysis (voice gate, tone/courtesy/heterodyne detection, DC-jump quick-key markers), Opus clip pipeline with voice-gated channel auto-creation; **milestone 3**: Whisper.net transcription with operator prompt + host-side callsign extraction; **milestone 5 core**: deterministic sessionization (90 s gap clustering), declared-window net classification, summary job triggering; **milestone 6**: LLamaSharp summaries from host-computed facts.
>
> Remaining: milestone 4 speaker embeddings (ONNX scaffold no-ops without a model file; needs ECAPA/WeSpeaker export + fbank features + clustering), milestone 5b recurrence mining (Mined nets), disruptive-vs-live-apply settings split in capture, per-channel fine DDC for off-grid carriers (12.5 kHz grid leaves ≤5 kHz offset on US 5 kHz-grid channels — DC removal absorbs it, band edges attenuate slightly), detector threshold tuning against real air, and UI surfacing for sessions/summaries.

1. **Capture daemon** — SDRPlay P/Invoke bindings, PFB channelizer, per-channel adaptive squelch, NBFM demod, Opus clips + marker events, local spool journal. IQ-file replay source from day one for regression tests.
2. **Web host + schema** — EF Core entities (Channels, Transmissions, Segments, Sessions, Speakers, Nets), FTS5 migration, event-ingest and job-queue API endpoints.
3. **Transcription worker** — Whisper.net, ham-vocabulary prompt seeding, phonetic→callsign post-processing, model-version stamping.
4. **Speaker clustering** — per-clip ECAPA embeddings (ONNX Runtime), session clustering, callsign labeling, within-clip split backstop.
5. **Net detection** — session clustering, weekly recurrence mining, deterministic net stats (roster, NCS, schedule, first-time check-ins).
6. **Summaries + dashboard** — LLamaSharp narrative summaries; Vue dashboard: live band view, FTS search with snippets + audio playback, net calendar.

## Open items (decide during build)

- Retention windows (e.g., Opus forever, raw debug captures 30 days) — config values, decide consciously at milestone 3.
- Capture in-container (USB passthrough + sdrplay service) vs. on host — attempt containerized first, host is the escape hatch.
- Whisper model size default (`small.en` vs `medium.en`) — measure accuracy on real captures at milestone 3.
- 8B vs 3B summary model — A/B once real net transcripts exist at milestone 6.
