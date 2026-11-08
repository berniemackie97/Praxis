# Standalone Praxis delivery plan

**Status:** Scoped roadmap for user review. No implementation, provider, privacy mode or target date is approved solely by this document.

## Correction to execution strategy

The M0–M7 repository milestones remain **capability gates**, not mandatory chronological implementation order. Previously, a first usable app was effectively blocked on two-device sync and Obsidian integration. That is incompatible with the user's confirmed requirement: Praxis must be independently useful as a local Second Brain.

Create a **local-owner vertical slice across M4 and M6** now. Preserve M0/M1 content integrity and architecture constraints. Build remote synchronization (M2), Obsidian acceptance (M3), full model assistance (M5) and operational scale-up (M7) as separately gated deliverables. Existing completed Obsidian archives remain useful recovery/interoperability evidence, not the entry point.

Project priority is independent from milestone number. Owner-local processing must remain fully local until processing policy is approved. Any non-loopback service, public endpoint, remote inference, credentialed connector, or multi-owner access is blocked on #13/#15/#50 and its own controls. A local prototype is **not** evidence of encryption, data confidentiality against malware, or production deployment readiness.

## Dependency-aware implementation slices

| Slice | Work | Deliverable and acceptance evidence |
| --- | --- | --- |
| S0 — local data contract | #99 | Review measured local catalog/storage alternatives, durable identity, crash/power/data-loss and privacy boundaries; proposed decision and fixtures |
| S1a — catalog bootstrap | #122 under #100 | Pin/lock SQLite provider, validate schema migration, owner-local directory, WAL/FK settings, rollback/reopen and incompatible-catalog refusal; **not yet a working import** |
| S1b — streamed original publication | #124 under #100 | Bound and verify input streams; non-overwriting same-volume publication before a transaction records original/source/item/first version; distinguish source identities despite byte deduplication |
| S1c–S1d — recovery and lifecycle | #100 | Add request idempotency, repeated-source versioning and orphan recovery, then prove consistent catalog+content backup/restore and disk/full-volume failure behavior |
| S2 — reliable ingestion | #102, #101 | File/folder Inbox import, visible states and retry/cancel; no original writes or duplicate logical entries |
| S3 — first extraction | #103 | Bounded text/Markdown and selected PDF extraction; other media metadata-only or unsupported, never discarded silently |
| S4 — rebuildable search | #106 | Lexical text search with source links; index rebuild, delete and version change tests |
| S5 — secure local access | #107 | Loopback-only independently callable service, authorization model for browser/MCP integration and negative origin/cross-process tests |
| S6 — usable Praxis app | #108 | First-party Inbox/Library/Search works end-to-end **without Obsidian**, no cloud account and no required model provider |

These are separate issue-sized implementation reviews, not an instruction to introduce a framework per row. A slice must re-audit actual code, dependencies and CI before implementation; one PR may cover multiple closely coupled acceptance points only when it remains reviewable. Cross-platform API/unit tests and a real macOS first-run acceptance are required before claiming the local MVP complete.

**Local MVP exit test:** Import a mixed folder (Markdown, text, selected PDF, image, video, code and unknown binary); preserve every accepted original byte and provenance; show unsupported processing; crash/restart/retry without duplicates; search supported text, inspect exact source, rebuild and delete the index; export/restore current originals; deny unauthorized or remote access. No Obsidian dependency, no silent external inference.

## Next capture experiences

| Lane | Issues | Demonstrable outcome |
| --- | --- | --- |
| Quick capture | #113 | Capture a typed or pasted note into the same durable Inbox |
| Dictation | #109, #110 | Explicit mic start/stop, preserve audio and produce an editable, attributed transcript where a processor is available |
| URL and browser | #104, #112, #111 | Source-attributed bookmark/page and selected content, paired least-privileged browser extension with offline queue |
| Opt-in folder watching and rich media | #105, #114 | Reliable rescan after watcher loss, safe media metadata and bounded extraction with unsupported-state reporting |

Voice, extension and URL fetch are not prerequisites for the initial S6 acceptance, but they are first-class backlog outcomes and should follow the local ingest/UI integration, not wait for Obsidian/mobile. Microphone recordings and fetched pages require explicit user action; external transcription must pass #50 consent.

## Organization, retrieval and agent progression

| Outcome | Issues | Gate |
| --- | --- | --- |
| Hybrid search and evaluation | #28, #29, #60 | Lexical baseline proven, corpus demonstrates relevance and absence of unauthorized/no-answer leakage |
| Reviewable tags/summaries | #115 under #97 | Proposal provenance, human override, source invalidation and abstention/quality tests |
| Projects and relationships | #116 under #97 | User-authored and inferred edges distinguished, transparent correction and deletion |
| MCP contract/security | #117 under #98 | Compatible transport, per-agent grants, explicit local trust boundary and attack cases reviewed |
| MCP read-only | #118 | Authorized search/fetch from an external agent with bounded citations and revocation |
| MCP capture/write | #119 | Separate explicit grants, approval when required, durable idempotent receipts; cannot be enabled on the strength of read-only tests alone |

Do not treat agent access as ordinary browser/UI access or grant tools by prompt instruction. Do not build a vector database or knowledge graph engine in anticipation of features that do not yet have measured workloads.

## Cross-cutting obligations

- **Data contract:** stable source identity, original content identity, revisions, provenance, processing versions, path safety, content hashes and distinct logical source ownership.
- **Trust:** local-only default; untrusted imports (PDF, HTML, code, archives, images) cannot execute, navigate local filesystem or authorize actions. Browser pairing, microphone capture and MCP each have a scoped trust boundary.
- **Lifecycle:** explicit ingest job states, retries, cancellation, observability, bounded quotas, crash recovery, deletion/revocation and restoration.
- **Retrieval:** index entirely rebuildable; maintain query and cached-result access controls; sources and generated summaries are visibly distinct.
- **UX:** unsupported is a first-class status. Empty first run, partial failure, cancel, stale results, storage-full and recovery are testable interactions, not documentation-only claims.
- **Verification:** test-driven negative cases and real first-party app smoke, not only protocol unit tests. No hidden remote calls or hardcoded provider credentials.
- **Delivery:** audit → bounded issue → tests/implementation → re-audit → docs → PR → green CI → owner review/merge → issue/board reconciliation. Close child work only after merge and evidence; keep parent release gates open until integrated end-to-end.

## Planning and owner decisions

- #99 evaluates embedded SQLite metadata and verified original files against an append-only JSONL alternative in [ADR 0017](../decisions/0017-local-catalog-and-original-storage.md); the recommendation remains subject to review, provider validation and end-to-end crash/backup tests under #100.
- #50 retains remote hosting, budget, multi-user access, data trust/key custody and external-processing decisions. Local no-cloud/no-agent prototype needs no implied consent for any of these.
- #30 defines later external source consent and availability; a manually supplied file does not require a third-party connector.
- #117 resolves the MCP SDK, transports and authorization boundary before writing MCP integration.
- #36/#63 retain full cross-platform UI-stack decisions; a first local browser UI does not freeze the eventual native/mobile strategy.

The declarative [GitHub project plan](../../scripts/github/roadmap.json) records native issue parents, scoped retired dependency edges, priorities and execution lanes. [Project setup](../project-setup.md) explains reviewed owner-side reconciliation and API quota safeguards. The private board's live custom fields cannot be claimed changed until that reconciliation succeeds.
