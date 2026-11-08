# Praxis product experience

**Status:** Product intent and acceptance direction. This document is not a list of shipped features.

Praxis is a local-first personal knowledge system that works **without Obsidian, a cloud account, or an AI provider**. It accepts owner-approved sources, keeps originals and source identity, makes supported content searchable, and lets the owner build meaningful connections across projects. Obsidian remains an optional editor/source adapter.

## Core experience

The owner can drag files, choose a folder, type or paste a thought, dictate a note, or explicitly save a web page. Approved integrations—including a browser extension, Obsidian and a future MCP server for agents—feed the same ingestion contract. An input folder is a convenient *inbox*, not the authoritative database and not a mandatory physical organization model.

Every intake item has visible, inspectable status: **received → safely stored → extraction pending/running → searchable** or **unsupported / failed / permission denied / canceled**. Processing may be retried or reindexed without losing the accepted original. Users should not need to interpret ZIP files or hidden directories to know what Praxis contains.

The first-party Praxis interface provides an Inbox, Library, Search, source detail, simple Projects/Collections and processing diagnostics. Search results link back to the exact source and explain whether the evidence came from original text, extracted text, a transcript or a derived suggestion. UI details and responsive layouts are design tasks, not assumptions about technology stacks.

## Original versus derived knowledge

| Layer | Owner-controlled meaning | Recovery and mutation policy |
| --- | --- | --- |
| Source | Where information came from: dropped file, chosen directory, URL, recorded voice, browser selection, connected app, editor or agent | Distinct source ID, origin, capture time, consent/scope and optional refresh policy |
| Original | Exact accepted content bytes or text, including unsupported formats | Immutable or versioned original, digest and bounded size; never silently rewritten by extraction or AI |
| Canonical item | Stable identity and versions associated with one or more sources | Cross-source duplicates can share stored bytes without merging provenance, permissions or deletion intent |
| Extraction | Text, metadata, image dimensions, transcript or parser output | Versioned by source digest and extractor; errors visible; regenerable |
| Index | Terms, snippets, eventually embeddings | Derived cache only, rebuilt and purged after source update/revocation |
| Suggested knowledge | Tags, topics, entities, summaries and inferred relationships | Model/rule provenance, status and confidence context; accepted user edits retained across reprocessing |
| User organization | Explicit projects, tags, saved searches, notes and relationships | Independent of the source directory and never overwritten by inferred categorization |

**No silent file moves.** Folders and suggested categories may be shown as views, not imposed by moving originals into a generated hierarchy. A file may relate to many projects, topics and people.

## Input types and truthful initial support

| Input | Initial accepted behavior | Deeper processing after validation |
| --- | --- | --- |
| Markdown / plain text | Preserve original, UTF-8 text extraction, searchable terms | Internal links, heading references, source-level entities |
| PDF / common documents | Preserve original; selected bounded PDF text extractor after evaluation | OCR, layout, tables and document-level citations |
| Images | Preserve bytes and safe file metadata; mark unprocessed where unsupported | OCR, visual captions and object/entity proposals with model consent |
| Recorded audio | Explicit record/stop, preserve original and processing status | Local or specifically approved transcription with editable versioned text |
| Video | Preserve original and supported container metadata | Transcripts, bounded keyframe analysis, chapters and scene indexing, separately justified |
| Source code / repositories | Preserve files and extensions; safe text extraction of supported code | Repository-aware symbols, references and relationships—never execute imported code |
| Game assets / archives / binaries | Preserve original, integrity digest and safe metadata; state unsupported analysis honestly | Opt-in format-specific parsers and limited extraction, never treat arbitrary binaries as understandable |
| URLs, pages and bookmarks | Explicitly capture source URL and permitted metadata; offline bookmark-only fallback | Safely fetched page snapshot, article text, link graph and refresh history |
| Other/unknown | Preserve or reject explicitly under size/path/security policy with visible reason | No invented content, summary or semantic claims |

Imported source content is **untrusted data**, including code comments, webpages and prompts embedded in documents. No source may grant agent privileges or instruct Praxis to access unrelated private resources.

## Capture surfaces

### Local Inbox (first delivery)

- Drop files or explicitly select directories; later opt into a specific watched intake folder.
- Preserve input location and bytes without modifying the source.
- Idempotent imports, separate source identity versus byte deduplication, and restart-safe job progress.
- Show unsupported processing as metadata-only rather than silently discarding files.

Work items: #99–#105, #114. The first usable local experience is #107–#108 and retrieval #106.

### Voice dictation

- Owner deliberately starts and stops a recording; microphone use is visible.
- Save raw audio first. Transcription can be pending, edited or declined independently.
- Record the transcription processor, model and language when applicable; local versus remote processing requires distinct consent.
- No always-listening microphone or silent remote audio upload.

Work items: #109–#110.

### Browser extension

- User invokes Save URL, Save Selection or Save Page. Request permissions narrowly.
- Browser/page content cannot call the local Praxis API as an authenticated user.
- Explicit local pairing, visible queue/retry status, safe URL and HTML handling, and no automatic browsing-history capture.
- URL-only capture does not imply a successful article extraction or license to archive restricted content.

Work items: #104, #111–#112.

### MCP and agents

- Agents use a **separate scoped interface**, not access to a vault path or the user's database.
- Start with paired, read-only search and fetch, returning stable IDs, bounds and source provenance.
- Capture or modify only via separately granted operations, independent authorization and audit/approval policy.
- Prompt injection in retrieved records cannot change permissions; revocation must immediately limit future access.
- Local-only comes first. Remote agent access, OAuth and host identity need explicit security design and device validation.

Work items: #117–#119, with #32 protecting write actions.

### Obsidian and other connectors

- Obsidian is an optional editor/capture adapter (#5/#57), not the start screen or source of truth.
- Later GitHub, calendar, email and document connectors (#30/#35) require specific scopes, retention and removal/refresh semantics.
- Cross-device sync (#4) is a distinct capability, not a prerequisite for capturing and searching content locally.

## Search, organization and trustworthy answers

First ship local, rebuildable lexical search with titles, paths, excerpts and source citations (#106). Hybrid/semantic retrieval follows with relevance evaluation and unauthorized-result tests (#28/#29/#60). Derived tags, entities and relationship candidates are reviewable (#97/#115/#116), not hidden source rewrites. Grounded research/chat (#33/#61) must cite exact inputs and abstain when evidence is missing. Authorized workflows (#32/#34/#62) never inherit permissions from model output or external source text.

## Required first local demonstration

1. Start Praxis on a supported desktop without installing or opening Obsidian and without network access.
2. Import a mixed, bounded test folder containing Markdown, one supported PDF, an image, a video, a code file and an unknown binary. Every accepted file is present with original digest and a transparent extraction state.
3. Restart mid-import; retry to a consistent catalog without duplicates or damaged originals. A second source holding identical bytes retains separate attribution.
4. Search a phrase from supported text and navigate from result to the exact source; unsupported media is discoverable by metadata only.
5. Rebuild the derived index and reproduce search results; delete/revoke a source and verify it disappears from results as policy requires.
6. Inspect and export/restore original bytes and metadata; distinguish full backups from a single-file restore.
7. Prove no private content is transmitted to an external host and that unrelated processes or browser pages cannot read the local service.

Subsequent slices add quick capture, dictation, browser extension, richer media, suggestions and agent access. None may silently expand remote trust or promise universal file comprehension.

## Not yet decided

The exact catalog/database, parser runtimes, local service packaging, microphone/transcription engine, UI framework, web-extension browser support, MCP SDK/transport and optional model provider must be selected through bounded spikes and evidence. Avoid premature microservices, vector databases, complex graphs or broad plugin abstractions before a measured use case requires them. Remote trust/hosting, key custody, account sharing and recovery choices remain in #50.

The implementation order and slice acceptance are in [standalone MVP plan](../delivery/standalone-mvp.md). Milestone/issue hierarchy is defined in [roadmap](../roadmap.md) and the [declarative GitHub plan](../../scripts/github/roadmap.json).
