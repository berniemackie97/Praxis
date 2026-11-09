# Standalone Praxis delivery plan

**Status:** Reviewed product direction and delivery plan. The standalone app remains unshipped; implementation and privacy decisions require their own verified acceptance.

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
| S1c — retry and interruption safety | #126 under #100 | Preserve source identity across retries and revisions, durable receipts and read-only orphan/corruption inventory; no destructive object collection |
| S1d — coherent local recovery | #128 under #100 | [Merged PR #134](https://github.com/berniemackie97/Praxis/pull/134) supplies coherent snapshot/restore and automated regressions. The [opt-in physical verification plan](../testing/physical-catalog-recovery.md) supplies a >2 GiB and reconnect acceptance path, but actual device, disk-full and unmount evidence remain open |
| S2 — automatic owner-local intake | #102, #101, #105 | Merged #135 supplies durable intake and processing contracts. The [automatic intake checkpoint](automatic-intake-mvp.md) adds a supervised watched folder and bounded extraction worker with no per-file command. Initial worker launch is still manual; first-party setup UI, rename-aware lifecycle and full source processing remain open |
| S3 — first extraction | #103 | S3a proposal adds bounded, digest-verified UTF-8 text/Markdown derivatives and explicit unsupported outcomes; PDF parsing and broader type/signature coverage remain open |
| S4 — rebuildable search | #106 | [Proposed schema-v5 search slice] adds FTS5 ranked literal-term retrieval, version/revocation-triggered invalidation, source attribution, bounded highlighted snippets and transactional derived-index rebuild; CI/review and UI integration remain unverified |
| S5 — owner-local trust and future integration | #129, #107 | Approved in-process or protected child-process first-party authority; #107's separately authenticated loopback integration host remains blocked independently and is not required for the first local UI |
| S6 — usable Praxis app | #108, #146 | First-party trusted Inbox/Library/Search and user-owned collections work end-to-end **without Obsidian**, a cloud account, a private HTTP listener or a required model provider |
| S7 — integrated acceptance | #130 | Real macOS first-run and cross-platform regression prove mixed-source import, provenance, recovery, indexed search, denial tests and user-visible errors |

These are separate issue-sized implementation reviews, not an instruction to introduce a framework per row. A slice must re-audit actual code, dependencies and CI before implementation; one PR may cover multiple closely coupled acceptance points only when it remains reviewable. Cross-platform API/unit tests and a real macOS first-run acceptance are required before claiming the local MVP complete.

**Local MVP exit test (#130):** While Praxis runs, automatically ingest files placed in an approved watched folder (without per-file commands); import a mixed folder (Markdown, text, selected PDF, image, video, code and unknown binary); preserve every accepted original byte and provenance; show unsupported processing; crash/restart/retry without duplicates; search supported text, inspect exact source, rebuild and delete the index; export/restore current originals; deny unauthorized or remote access. No Obsidian dependency, no silent external inference.

## Status checkpoint — November 9, 2026

Merged PRs #144, #145, #147 and #148 now provide a first macOS workbench with supervised automatic intake, named collections and direct note capture. These are implementation milestones, not proof of the end-to-end first-use acceptance: #130 remains open for owner-run macOS GUI and mixed-corpus/restart/recovery/security verification. #146 remains open for unverified collection acceptance even though its implementation is merged. CI status and hardware verification must be read live rather than inferred from merge state.

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

- #99 evaluates embedded SQLite metadata and verified original files against an append-only JSONL alternative in [ADR 0017](../decisions/0017-local-catalog-and-original-storage.md); provider validation and basic storage behavior are now implemented in merged S1a–S1d software slices; [ADR 0018](../decisions/0018-coherent-local-backup.md) specifies the S1d snapshot proposal and the still-unverified physical recovery gates.
- #50 retains remote hosting, budget, multi-user access, data trust/key custody and external-processing decisions. Local no-cloud/no-agent prototype needs no implied consent for any of these.
- #30 defines later external source consent and availability; a manually supplied file does not require a third-party connector.
- #117 resolves the MCP SDK, transports and authorization boundary before writing MCP integration.
- #36/#63 retain full cross-platform UI-stack decisions; a first local browser UI does not freeze the eventual native/mobile strategy.

The declarative [GitHub project plan](../../scripts/github/roadmap.json) records native issue parents, scoped retired dependency edges, priorities and execution lanes. [Project setup](../project-setup.md) explains reviewed owner-side reconciliation and API quota safeguards. The private board's live custom fields cannot be claimed changed until that reconciliation succeeds.

## First-party macOS checkpoint and execution dependencies

[Draft PR #145](https://github.com/berniemackie97/Praxis/pull/145) proposes a native macOS workbench that supervises the existing automatic .NET worker through private child-process pipes, rather than requiring a source-bearing HTTP host. It adds first-run catalog/Inbox setup and an initial Library/Search/preview workflow; actual UI and new protocol test execution remain unverified. [#146](https://github.com/berniemackie97/Praxis/issues/146) owns first-use user-authored collections. Enrichment proposals and inferred relationships remain distinct #115/#116 work and are not an implied capability of the watcher.

Under approved ADR 0021, #107 is an **external integration** dependency and may proceed separately after its storage/security gates; it does not block #108 or local-only #130. Declarative planning proposes retiring blocked-by #101/#107 for #108 and #107 for #130, preserving #101's own outstanding first-use acceptance. #107 moves from L0 to L3; #146 enters L0. The GitHub Project custom Status/Priority/Stage must be reconciled with these scoped changes by the owner until project-write access is available. No parent issue closes from an unverified draft.


## Owner-authored first-use collections — #146

The first-party workbench now has a draft schema-v6 and UI integration for named owner-owned collections, source membership, archive and collection-filtered browsing. Collections are explicit source-ID assignments, not filesystem folders or AI classifications. The data follows coherent catalog snapshots and remains recoverable alongside preserved originals. Source revision updates retain membership; source revocation excludes it from active collection views. This draft is not a passed local build, three-OS CI result or GUI acceptance.

The proposed agent-assisted continuation is #115's separately authorized **proposal** lifecycle after deterministic extraction. Any rule/model-enriched suggestion must be stamped with source/item/revision/digest/processor identity, bounded in runtime and output, reviewable and reversible, never silently accepted or promoted into canonical owner-authored collection assignments. #116 owns richer relations; #143 owns scoped source-backed agent context packs. The initial release works with all inference disabled.


## First-use quick note checkpoint (#113)

Typed or intentionally pasted notes are a P1/L0 first-use requirement, not a prerequisite to separately gated voice/browser integrations. Draft implementation in the quick-note feature branch writes a bounded owner-authored Markdown file atomically into the **already-approved watched Inbox**, then commits the same exact bytes through the canonical original object and source-revision catalog. The existing text worker indexes that saved source; collection assignment and search are the same as for other approved Markdown files. No clipboard watcher, third-party service, URL fetch or agent model is involved.

The app provides an optional title, a text editor, Command-Return save, explicit acknowledgement and a retryable capture token while the process remains alive. A title is an authored Markdown heading, not an inferred label. Note file publication is owner-private on POSIX; root selection still obeys the approved watched-source boundary. Source files remain in the opted-in Inbox, and removing them from that folder does not automatically revoke preserved originals. The initial service uses `file` source identities rather than introducing a competing quick-note database.

After source-preserving capture and owner collections are usable, the separately controlled agentic continuation is #115: version- and digest-fenced proposed tags/topics/summaries from eligible verified extracted content, human review and abstention, followed by #143's bounded source-attributed context packs and #117–#119's explicit agent grants. This does not block note capture when no model is installed.

The parent workbench #108 and integrated release #130 remain open until an owner-facing end-to-end scenario passes on actual macOS hardware. This draft is not an executed UI build or green cross-platform CI.

## Subsequent knowledge-intelligence and connected-history lanes

The first local deliverable intentionally stops short of autonomous technical reasoning. The defining longer-term product objective is to turn authorized technical artifacts, ASM/decompilation, research, repositories and notes into a linked, evidence-grounded technical knowledge base, then answer questions that demand synthesis rather than filename lookup.

- **L4 — Technical evidence and investigation:** #149/#152–#154 choose and implement bounded technical extraction, explicit claims and contradictions; #150/#155/#156 evaluate limited investigation loops, citations and abstention. #29/#31/#33/#97 remain the common retrieval/model/chat and review components.
- **L5 — Connected history and interaction:** #151/#157 introduce typed provider events and replay-safe change history; #158 captures approved GitHub development history, #159 scoped local session transcripts, #162 investigates financial/social APIs and privacy; #160 is a separately approved tool proposal workflow, while #161 adds optional free local conversational voice.
- **Cost policy:** deterministic extraction/event classification before model inference; measured local Ollama/Qwen path first; Claude/Codex only through supported, explicitly consented invocation with input/cost disclosure; no assumption that user subscriptions include API use.
- **Security gate:** model output never grants authority. Provider accounts, local session histories and command execution require distinct, narrow, revocable consent. Sensitive financial and social account access remains a research decision, not a scheduled or implemented connector.

Do not create a graph database, broad tool runtime, uncontrolled session monitor, provider marketplace or autonomous background agent before a measured vertical slice justifies it. The owner can use L0 entirely offline with no AI provider; L4/L5 only exit when proven against real authorized cases.
