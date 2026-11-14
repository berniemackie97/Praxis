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

Audio and video originals (`.mp4 .mov .m4v .m4a .m4b .mp3 .wav .aac .aif .aiff .caf .flac`) are transcribed by the read stage on device through the bundled helper (`praxis-vision transcribe`, Apple SpeechAnalyzer/SpeechTranscriber on macOS 26; no network for content, no permission prompt, the speech model is installed once by the OS). The helper reads the verified original through a private symbolic link named with its container's extension (no copy of large videos). The transcript is stored as the source's **readable text** (a paragraph per pause), and the time range of every utterance is stored separately as character spans (`text_timings`), so quotes, search, segments, triage, understanding and records work on natural language while any character still maps back to its moment. Locale defaults to the system locale (`PRAXIS_TRANSCRIPTION_LOCALE` overrides).

- Long media gets a time budget from its size (recognition runs far faster than real time) and the read stage renews its lease while the helper runs. Containers AVFoundation cannot decode (`.webm`, `.mkv`) keep the sidecar caption path.
- Digest-bound sidecar captions keep working; moments cut from them play the paired video.
- A silent recording closes as `no_speech`. When the speech model is not installed yet, the job is deferred and retried. Without the helper or on older macOS, media stays unsupported and is reopened automatically once transcription is available.

### 2. Moments: clip-level retrieval

A **moment** is a time range in one media source plus its transcript. `MediaMoments` turns retrieval hits into moments in code: it maps a hit's character offset to cues, merges adjacent cues into a coherent span (sentence boundaries, at most 3 minutes, small lead-in and tail padding) and returns `time:<start>-<end>` locators (seconds, millisecond precision). Retrieval uses the existing hybrid search, so "where do they configure the database" finds the cue by meaning, not just words.

Agent tool `media_find` returns moments (query, optional source) or a whole-media reference (source only). Every moment is ordinary numbered evidence; `knowledge_open` accepts `time:` locators.

### 3. Playback in chat

Citations that resolve to a media source render as an inline player in chat: the clip plays from its start and stops at its end, with play/pause, scrubbing, the transcript line, **Play full video** (removes the bound and keeps the position) and **Open in QuickTime Player**. The app asks the worker for a playback reference (`mediaPlayback`): the watched file when it still matches the cited revision's digest, otherwise the preserved original from the store with its type. Downloaded web videos play locally; linked-only online videos play through the provider's privacy-enhanced embed with start/end parameters, which is owner-initiated network use at view time.

### 4. Saved links: owner intent, safe fetching

**What gets fetched.** Links the owner saved: `.webloc` (XML or binary property list) and `.url` files, links added with **Save Link…** (⌘⇧L), and links the owner pastes in chat and asks the agent to save (`knowledge_save_link`). Saved links are written as standard `.webloc` files in "Praxis Links" inside the primary intake folder. URLs merely *mentioned* in documents are **never** fetched: fetching links from mail and third-party documents confirms receipt to trackers and can trigger one-click actions (unsubscribe, confirm).

**Prompt-injection guard.** The agent may save a link only if that exact URL appears in an owner message in the same conversation. A document cannot make the agent fetch an attacker URL with private data in its query string.

**Safe fetcher.** `https`/`http` only, no user info in the URL; every connection resolves the host itself and connects to a checked address (no rebinding between check and use); a host resolving to any loopback, private, link-local, CGNAT, multicast, documentation, benchmarking or reserved address (IPv4 and IPv6, including IPv4-mapped and NAT64 forms) is refused; redirects are followed by hand (at most 5, each re-checked); no cookies, credentials or proxies, a generic user agent; response size (16 MB) and time (30 s) bounded; allowed types: HTML, text, Markdown, JSON, PDF and images.

**Snapshot capture.** A fetch writes a snapshot into "Praxis Web" inside the primary intake folder (Markdown with title, canonical URL, fetch time and readable page text; PDFs and images kept as fetched) and captures it as a normal source, which then flows through the generic intake. The link source shows its snapshot; re-fetching is explicit (owner or `intake_reprocess`).

**Online video links.** YouTube links become a `.webvideo` snapshot: public metadata and, when the owner has `yt-dlp` installed (Homebrew paths or `PRAXIS_YTDLP`), the published English captions (subtitles only, the video is not downloaded; rolling auto-captions are de-duplicated). The web-video reader stores them as transcript text with timing, so `media_find` cuts moments from linked videos, and chat plays them through the privacy-enhanced embed (youtube-nocookie.com) with start/end bounds. Without `yt-dlp`, the oEmbed title and channel are kept so the link stays searchable.

The link-fetch stage (`link-fetch-v1`) is scheduled only for sources the read stage read as format `link`. Settings → Privacy → Saved links holds the mode (`saved_links`, the default, or `off`) and blocked hosts (subdomains included). Failed fetches close with a reason (`private_address`, `unreachable`, `http_error`, `unsupported_content`, `too_large`, `host_blocked`, `links_off`); `intake_reprocess` fetches again.

### 5. Code projects: understood as a whole

**Capture hygiene.** Watched-folder scanning skips dependency and build output (`.git`, `node_modules`, `bin`, `obj`, `.build`, `DerivedData`, `build`, `dist`, `target`, `__pycache__`, `.venv`, `Pods`, `.gradle`, `.next`, `vendor`, …), honors `.gitignore` files (comments, negation, anchoring, directory-only and `**` patterns), and never captures secrets files (`.env*`, private keys). This also stops one repository from pushing a watched folder past its file limit.

**Per-file cost.** Code files are not sent to a model one by one (a repository can hold thousands). The read stage keeps them searchable and openable; understanding records a code-computed outline (symbols per file for C#, Swift, TypeScript/JavaScript, Python, Go, Rust, Java/Kotlin) with zero tokens.

**Project stage.** A project root is the outermost folder holding a captured marker (`*.sln`, `*.slnx`, `*.csproj`, `package.json`, `Cargo.toml`, `go.mod`, `pyproject.toml`, `setup.py`, `Package.swift`, `pom.xml`, `build.gradle*`, `Gemfile`, `composer.json`, `CMakeLists.txt`, …); nested markers belong to it, so a monorepo is one project. Code builds a bounded **repository map** (at most 60k characters): the file tree, languages by size, dependencies and commands parsed from manifests, the README and per-file outlines. One strong-tier call returns purpose, summary, components (name, path, role), entry points and build commands. Code verifies it: every component path and entry point must exist in the tree, every command must be declared by a manifest or written in the README, and dependencies come only from manifests. More than a quarter failing escalates to the strongest tier; the verified subset is kept otherwise. Only files whose category may leave for code work (code, documents; never local-only) appear in the map. A background pass every 5 minutes re-derives a project when its fingerprint (member paths and digests) changes, at most once per 15 minutes per project.

Agent tool `knowledge_project` lists projects and opens one (summary, components, entry points, dependencies, tree excerpt), with files as evidence.

### 6. Records in chat

A `records` dataset exposes verified events, deadlines and contacts from current, active, disclosable revisions to `data_describe`/`data_query` (kind, title, date, end, location, amount, currency, name, role, organization, email, phone, tier, source), with the evidence quote as citation. "What's due next week", "when is my dentist appointment", "who is my contact at the bank" become exact queries. `knowledge_timeline` includes events and deadlines in its period.

### 7. Persistence (schema v24)

- `text_timings`: per transcript revision, the character span and time range of each utterance.
- `link_snapshots`: link revision → captured snapshot locator, final URL, HTTP status, content type, fetch time, outcome or reason.
- `link_settings`: mode and blocked hosts (one row).
- `intake_projects`: project root, fingerprint, status, verified details (JSON), member count, tier, provider, tokens, updated time.
- Upgrade queues one fetch for `.webloc`/`.url` sources captured before v24. Schema v25 (model settings) stacks after this version.

## Consequences

- Every spoken word in the owner's media becomes citable at the second, and the agent answers with clips, not files.
- Links the owner saves are understood like documents; nothing the owner did not ask for is fetched.
- Repositories are understood at project level for a few thousand tokens instead of one call per file.
- Transcription depends on macOS 26 speech assets; on older systems media stays searchable only through captions, and the intake panel says so.

## Follow-ups

- Keyframe OCR for slides and on-screen code in tutorials (same moment contract, `frame:` cues).
- Speaker diarization when the platform provides it.
- Fetching and understanding a linked code repository (clone into a Praxis-owned folder behind the same link policy).
- Owner opt-in to fetch links found in the owner's own notes (never email), and downloading linked videos for offline playback.
- Committing project components and dependencies as knowledge-graph proposals, and a projects view in the app.
- An explicit approval flow for policy-style audit of link-setting changes.
