# ADR 0016: Candidate standalone local source-processing contract

**Status:** Proposed for #99; **not accepted**, not implemented.

## Context

The first Praxis prototype proved stable note IDs, immutable revision history, and byte-exact local archive recovery through an optional Obsidian desktop adapter. It did **not** produce a standalone searchable Second Brain. The owner's priority is an independent Praxis Inbox for arbitrary accepted originals, followed by honest type-aware processing and retrieval. A raw folder is one possible source, not an authoritative data model.

## Proposed responsibilities

- **Explicit source**: owner-approved root, file, URL, recording, browser selection or agent grant; source ID, observed path/URI, capture time and consent/retention scope.
- **Original**: preserved exact bytes or submitted text, content digest and version. Identical bytes may share underlying content but never merge distinct source ownership or revoke another permitted source.
- **Canonical item**: durable ID and current-version pointer independent of file path, plugin or client. Reimports produce deterministic outcomes; missing/changed source input does not silently delete or overwrite a record.
- **Processing run**: persisted source version, extractor ID/version, job state, bounded resources, retry/cancellation and diagnostics. An error is not a successful extraction.
- **Derived record**: searchable text, metadata, transcript or suggested entities/tags with source reference and processor/model identity; fully rebuildable and invalidated after source changes or permission revocation.
- **User-authored knowledge**: corrected tags, notes, relationships, collections and approvals remain independently owned, with explicit merge/conflict rules.

The first local interface should display this lifecycle rather than require raw ZIP inspection. Agents and browser extensions consume a deliberately scoped capture/query contract, not direct filesystem or database access. Voice saves an explicit user-started audio original before transcription.

## Mandatory negative cases before acceptance

- Unreadable, changing, huge, malformed or symlinked files; archive/decompression bombs; unsupported media; full disk; interrupted writes and process kills.
- Importing identical bytes from two different sources and retrying the same source after restart.
- Source deletion and revocation, stale index hits, derived suggestions that contradict originals and old versions.
- Localhost API requests from hostile websites, unrelated local users/processes and unauthorized agents.
- Remote transfer and inference attempts without per-source consent, even when the endpoint appears available.
- Microphone cancellation, no permission, recording loss, offline transcriber and transcript correction.

## Design decisions to settle in #99

Compare a small embedded catalog/store design, including SQLite or other local engines, against durability, migration and recovery constraints; do not treat the choice as approved by this document. Specify exact source/item/original schemas, background work lifecycle, file system/publication semantics, authorized local calling process, retention/forget behavior, portable export and steady-state disk/memory bounds.

Follow [standalone implementation lanes](../delivery/standalone-mvp.md). Do not select remote hosting, encryption mode, speech/LLM provider, vector store or graph engine by implication; those choices remain separately gated in #50/#117 and feature-level evidence.
