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
| **Deterministic net facts, LLM for prose only** | Check-ins, net control, schedule, and durations are database queries + recurrence mining — reliable and testable. The local 8B-Q4 model (LLamaSharp) only writes the narrative summary as a post-net batch job (2–4 min/net on CPU is irrelevant there). No cloud LLM: hard offline requirement. |

## Milestones

1. **Capture daemon** — SDRPlay P/Invoke bindings, PFB channelizer, per-channel adaptive squelch, NBFM demod, Opus clips + marker events, local spool journal. IQ-file replay source from day one for regression tests.
2. **Web host + schema** — EF Core entities (Channels, Transmissions, Segments, Sessions, Speakers, Nets), FTS5 migration, event-ingest and job-queue API endpoints.
3. **Transcription worker** — Whisper.net, ham-vocabulary prompt seeding, phonetic→callsign post-processing, model-version stamping.
4. **Speaker clustering** — per-clip ECAPA embeddings (ONNX Runtime), session clustering, callsign labeling, within-clip split backstop.
5. **Net detection** — session clustering, weekly recurrence mining, deterministic net stats (roster, NCS, schedule, first-time check-ins).
6. **Summaries + dashboard** — LLamaSharp narrative summaries; Vue dashboard: live band view, FTS search with snippets + audio playback, net calendar.

## Open items (decide during build)

- Retention windows (e.g., Opus forever, raw debug captures 30 days) — config values, decide consciously at milestone 3.
- Whisper model size default (`small.en` vs `medium.en`) — measure accuracy on real captures at milestone 3.
- 8B vs 3B summary model — A/B once real net transcripts exist at milestone 6.
