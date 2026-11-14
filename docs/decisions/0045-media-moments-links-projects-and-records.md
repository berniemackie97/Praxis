# ADR 0045: Media moments, saved links, code projects and records in chat

**Status:** Accepted (lead-architect decision with owner, November 13, 2026). Schema v24. Builds on [ADR 0044](0044-generic-intake-engine.md) (generic intake) and [ADR 0043](0043-tiered-intake-disclosure-and-agent-runtime.md) (tiers, disclosure, agent runtime). Closes the follow-ups ADR 0044 listed: audio transcription, links, code projects and agent access to records.

## Context

The owner keeps hundreds of videos (tutorials, recorded talks, downloads) and links to online videos, saves web links, keeps code projects in watched folders, and expects to ask the agent "show me the part where they configure the database" and get *that clip*, playable in chat, with the full video one click away. Today:

- Videos are searchable only through sidecar caption files whose name carries the video digest; audio is not transcribed.
- `.webloc`/`.url` files are read as text; nothing is fetched.
- A code repository is a pile of unrelated files, and a watched folder containing `node_modules` or `.git` exceeds the scan limits and fails as a whole.
- Events, deadlines and contacts from intake are stored but the agent cannot query them.

## Decision

### 1. Transcription is a reader (on device, zero tokens)

Audio and video originals (`.mp4 .mov .m4v .m4a .mp3 .wav .aac .aif .aiff .caf .flac`) are read by a **media reader** that transcribes speech on device through the bundled helper (`praxis-vision transcribe`, Apple SpeechAnalyzer/SpeechTranscriber on macOS 26; no network, no permission prompt, models installed once by the OS). The read stage stores the transcript as the source's text in WebVTT form, so every existing capability (search, segments, citations, triage, understanding, records) works on spoken content with no new path. Each cue keeps its exact time range. Locale defaults to the system locale and is owner-configurable.

- Long media gets a time budget proportional to its duration; the read stage's lease covers it. Containers AVFoundation cannot decode (`.webm`, `.mkv`) keep the sidecar caption path.
- Sidecar captions (`video.mp4.vtt`, or the digest-bound form) take precedence when present, because creators' captions beat recognition.
- Without the helper or on older macOS, media sources wait with `transcription_unavailable` (visible), never silently skipped.

### 2. Moments: clip-level retrieval

A **moment** is a time range in one media source plus its transcript. `MediaMoments` turns retrieval hits into moments in code: it maps a hit's character offset to cues, merges adjacent cues into a coherent span (sentence boundaries, at most 3 minutes, small lead-in and tail padding) and returns `time:<start>-<end>` locators (seconds, millisecond precision). Retrieval uses the existing hybrid search, so "where do they configure the database" finds the cue by meaning, not just words.

Agent tool `media_find` returns moments (query, optional source) or a whole-media reference (source only). Every moment is ordinary numbered evidence; `knowledge_open` accepts `time:` locators.

### 3. Playback in chat

Citations that resolve to a media source render as an inline player in chat: the clip plays from its start and stops at its end, with play/pause, scrubbing, the transcript line, **Play full video** (removes the bound and keeps the position) and **Open in QuickTime Player**. The app asks the worker for a playback reference (`mediaPlayback`): the watched file when it still matches the cited revision's digest, otherwise the preserved original from the store with its type. Downloaded web videos play locally; linked-only online videos play through the provider's privacy-enhanced embed with start/end parameters, which is owner-initiated network use at view time.

### 4. Saved links: owner intent, safe fetching

**What gets fetched.** Links the owner saved: `.webloc`, `.url` and `.weblink` files, links added in the app, and links the owner pastes in chat and asks the agent to save. URLs merely *mentioned* in documents are **not** fetched by default: fetching links from mail and third-party documents confirms receipt to trackers and can trigger one-click actions (unsubscribe, confirm). An owner setting can widen this to links in the owner's own notes; email is never auto-fetched.

**Prompt-injection guard.** The agent may save a link only if that exact URL appears in an owner message in the same conversation. A document cannot make the agent fetch an attacker URL with private data in its query string.

**Safe fetcher.** `https`/`http` only; DNS resolved once and the connection pinned to the resolved address (no rebinding); loopback, private, link-local, CGNAT, multicast and reserved ranges refused (IPv4 and IPv6, including mapped forms); redirects followed manually (at most 5, each re-checked); no cookies, no credentials, fixed generic user agent; response size (16 MB) and time (30 s) bounded; allowed types: HTML, text, Markdown, JSON, PDF, images. Per-host pacing.

**Snapshot capture.** A fetch writes a snapshot into "Praxis Web" inside the primary intake folder (Markdown with title, canonical URL, fetch time and readable page text; PDFs and images kept as fetched) and captures it as a normal source, which then flows through the generic intake. The link source shows its snapshot; re-fetching is explicit (owner or `intake_reprocess`).

**Online video links.** For video hosts, the snapshot uses the provider's public metadata (oEmbed) and, when the owner has `yt-dlp` installed, the published captions (`--skip-download`, subtitles only), stored as timed cues so moments work on linked videos without downloading them. Downloading the video itself for offline playback is a separate, default-off owner setting; the owner is responsible for having the right to download.

The link-fetch stage (`link-fetch-v1`) runs after the read stage and is a no-op for anything that is not a saved link. The setting lives with the disclosure policy (mode: `off`, `saved_links` (default), `saved_and_notes`; blocked hosts) and changes are audited like other policy changes.

### 5. Code projects: understood as a whole

**Capture hygiene.** Watched-folder scanning skips dependency and build output (`.git`, `node_modules`, `bin`, `obj`, `.build`, `DerivedData`, `build`, `dist`, `target`, `__pycache__`, `.venv`, `Pods`, `.gradle`, `.next`, `vendor`, …), honors `.gitignore` files (comments, negation, anchoring, directory-only and `**` patterns), and never captures secrets files (`.env*`, private keys). This also stops one repository from pushing a watched folder past its file limit.

**Per-file cost.** Code files are not sent to a model one by one (a repository can hold thousands). The read stage keeps them searchable and openable; understanding records a code-computed outline (symbols per file for C#, Swift, TypeScript/JavaScript, Python, Go, Rust, Java/Kotlin) with zero tokens.

**Project stage.** A project root is a folder with a marker (`.git`, `*.sln`, `*.csproj`, `package.json`, `Cargo.toml`, `go.mod`, `pyproject.toml`, `Package.swift`, `pom.xml`, `build.gradle*`, `Gemfile`, `composer.json`). Code builds a bounded **repository map**: the file tree, languages by size, dependencies parsed from manifests, the README and the per-file outlines. One strong-tier call returns the project's purpose, architecture, components (name, path, role), entry points and how to build and run it. Code verifies it: every path must exist in the tree, every build command must come from the README or a manifest script, dependencies come only from manifests. Failures escalate to the strongest tier, as in ADR 0044. The result is re-derived when the project's fingerprint (member paths and digests) changes, at most once per 15 minutes per project; components and dependencies are committed as knowledge proposals (project → component, project → dependency).

Agent tool `knowledge_project` lists projects and opens one (summary, components, entry points, dependencies, tree excerpt), with files as evidence.

### 6. Records in chat

A `records` dataset exposes verified events, deadlines and contacts from current, active, disclosable revisions to `data_describe`/`data_query` (kind, title, date, end, location, amount, currency, name, role, organization, email, phone, tier, source), with the evidence quote as citation. "What's due next week", "when is my dentist appointment", "who is my contact at the bank" become exact queries. `knowledge_timeline` includes events and deadlines in its period.

### 7. Persistence (schema v24)

- `intake_projects`: project root, fingerprint, status, summary JSON, tier, provider, tokens, updated time.
- `link_settings` in the policy store (mode, blocked hosts) with audit.
- `link_snapshots`: link source revision → captured snapshot locator, final URL, HTTP status, content type, fetch time, outcome.
- Cue times for transcripts are derived from the stored WebVTT text; no new tables.

## Consequences

- Every spoken word in the owner's media becomes citable at the second, and the agent answers with clips, not files.
- Links the owner saves are understood like documents; nothing the owner did not ask for is fetched.
- Repositories are understood at project level for a few thousand tokens instead of one call per file.
- Transcription depends on macOS 26 speech assets; on older systems media stays searchable only through captions, and the intake panel says so.

## Follow-ups

- Keyframe OCR for slides and on-screen code in tutorials (same moment contract, `frame:` cues).
- Speaker diarization when the platform provides it.
- Fetching and understanding a linked code repository (clone into a Praxis-owned folder behind the same link policy).
