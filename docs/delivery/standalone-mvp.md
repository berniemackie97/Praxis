# Local Praxis foundation and intelligent MVP delivery

**Status:** Foundation implementation and broader product intent; merged individual slices do not establish end-to-end hardware/release acceptance. **#130 is a local foundation checkpoint. #170 is the real agent-powered intelligent Second Brain MVP release gate.**

## Correction to execution strategy

The M0–M7 repository milestones remain **capability gates**, not mandatory chronological implementation order. Previously, a first usable app was effectively blocked on two-device sync and Obsidian integration. That is incompatible with the user's confirmed requirement: Praxis must be independently useful as a local Second Brain.

Create an owner-local foundation across M4/M6, then verify the integrated intelligence, **native first-party evidence graph**, and governed task release gate #170. Preserve M0/M1 integrity and authorization, and build remote synchronization (M2) and unrelated device parity separately. **Native Knowledge Graph acceptance #388 and verified local-first agent/model assistance are mandatory for #170**. The M3 optional Markdown/vault feature #57/#171 and historical Obsidian archives remain independent interchange/recovery work, not an MVP release gate.

Project priority is independent from milestone number. Owner-local processing must remain fully local until processing policy is approved. Any non-loopback service, public endpoint, remote inference, credentialed connector, or multi-owner access is blocked on #13/#15/#50 and its own controls. A local prototype is **not** evidence of encryption, data confidentiality against malware, or production deployment readiness.

## November 13, 2026 — M3 native graph and optional portability

The owner replaced the former Obsidian-dependent M3 goal with **native knowledge exploration and portable Markdown interchange**. [#5](https://github.com/berniemackie97/Praxis/issues/5) owns native graph feature #383 and optional interchange #57; #58/#24 move to first-party clients under #8/M6. #384 renderer and #385 bounded source-verified graph API can be planned concurrently; #386 visual exploration, #387 evidence/owner review and #388 verified macOS accessibility/performance/privacy complete the native graph gate. **#388 blocks #170; #171 does not.** The historical Obsidian desktop and mobile-plugin enhancement tails #22/#23 are not planned and should not be confused with completed portability or certified mobile clients. The graph visualizes canonical knowledge but neither stores another canonical graph nor grants unauthorized relationship edits.

## Two distinct gates — do not conflate them

**L0 / #130:** Launch, capture, preserve, index, inspect, organize and recover local knowledge reliably. It must function offline without an LLM or Obsidian. This is essential engineering foundation but **not** a complete MVP.

**Intelligent MVP / #170:** Take a representative **mixed technical, multimedia and financial corpus** from raw sources through trustworthy processing and relationships, retrieve and investigate complex questions with grounded answers (including timecoded video playback and a reconciled editable budget), run a bounded owner-approved task, **explore source-verified relationships natively in Praxis** (#383/#388) and use measured local-first inference with opt-in external escalation. The deliverables are #149/#150, #29/#31/#33/#172, #143, #32/#34/#160/#173, and #383/#388; optional #57/#171 interchange does not gate #170. Reuse the same canonical storage, consent policy and user experience.

Require three distinct validated question-to-artifact workflows for the same MVP #170: (1) cross-source technical synthesis, (2) tutorial video excerpt playable in chat with exact source/time range and full-file navigation (#177), (3) deterministic month budget computed from authorized structured bills, income and transactions with an editable output (#185). #176 owns domain processing and #179 the cross-domain gold fixtures. Local financial imports satisfy the first release without live banking API credentials; all private sources remain scoped and revocable.

Design validation on actual source/domain questions, not superficial generated summaries. Never declare production readiness without performance, grounding, redaction, revocation, no-answer, agent injection, failure/restart and owner hardware evidence.

## Dependency-aware implementation slices

| Slice | Work | Deliverable and acceptance evidence |
| --- | --- | --- |
| S0 — local data contract | #99 | Review measured local catalog/storage alternatives, durable identity, crash/power/data-loss and privacy boundaries; proposed decision and fixtures |
| S1a — catalog bootstrap | #122 under #100 | Pin/lock SQLite provider, validate schema migration, owner-local directory, WAL/FK settings, rollback/reopen and incompatible-catalog refusal; **not yet a working import** |
| S1b — streamed original publication | #124 under #100 | Bound and verify input streams; non-overwriting same-volume publication before a transaction records original/source/item/first version; distinguish source identities despite byte deduplication |
| S1c — retry and interruption safety | #126 under #100 | Preserve source identity across retries and revisions, durable receipts and read-only orphan/corruption inventory; no destructive object collection |
| S1d — coherent local recovery | #128 under #100 | Merged #134 and #197 deliver coherent catalog-plus-originals snapshots and owner-invokable offline backup/verify/restore; opt-in physical verification tooling exists. Real unplug, disk-full and >2 GiB device acceptance remain open (#128) |
| S2 — automatic owner-local intake | #102, #101, #105 | Merged #135/#144/#168/#169/#202/#213 implement independently leased processing, supervised watched-folder capture, health/pause/retry, conservative observed moves and canonical path identity. The first-party app configures the watched Inbox; physical watcher/overflow/volume tests are still open (#105) |
| S3 — first extraction | #103 | Merged #138 adds bounded original-verified text derivatives; #175 adds supported code/listing text and #193 narrowly inspects verified video headers. PDF breadth and fuller media processing are still open (#103/#114) |
| S4 — rebuildable search | #106 | Merged #140 provides revision-fenced FTS5 local search and deterministic reindex. Hybrid/structural retrieval quality and cited answer evaluation remain #29/#172/#179, not a claim of completed RAG |
| S5 — owner-local trust and future integration | #129, #107 | Approved in-process or protected child-process first-party authority; #107's separately authenticated loopback integration host remains blocked independently and is not required for the first local UI |
| S6 — usable Praxis app | #108, #146, #215 | Merged #145/#147/#148/#167/#212 and native Xcode #216–#220 implement a private worker-backed SwiftUI application with Inbox/Library/Search, collections, quick notes, original export and knowledge/model screens. Successful owner-Mac build, launch, app-hosted tests and first-use UX remain unverified (#215/#130) |
| S7 — integrated acceptance | #130 | Open: actual Mac first-run, mixed-source capture/search/restart/recovery/denial and current Windows/Linux/macOS automated acceptance. CI blocked by unprovisioned runners (#137) |

These are separate issue-sized implementation reviews, not an instruction to introduce a framework per row. A slice must re-audit actual code, dependencies and CI before implementation; one PR may cover multiple closely coupled acceptance points only when it remains reviewable. Cross-platform API/unit tests and a real macOS first-run acceptance are required before claiming the local MVP complete.

**Local MVP exit test (#130):** While Praxis runs, automatically ingest files placed in an approved watched folder (without per-file commands); import a mixed folder (Markdown, text, selected PDF, image, video, code and unknown binary); preserve every accepted original byte and provenance; show unsupported processing; crash/restart/retry without duplicates; search supported text, inspect exact source, rebuild and delete the index; export/restore current originals; deny unauthorized or remote access. No Obsidian dependency, no silent external inference.

## Status checkpoint — November 9, 2026

Merged #144–#220 have advanced the owner-local app, including bounded local Ollama enrichment (#205/#206), entity relations and owner governance (#207), owner-approved stronger CLI checks (#210) and first-party Knowledge/Verification/Models views (#212). The native Xcode project and follow-up packaging/compiler fixes (#216–#220) are on `main`, but #215 has no verified successful owner-Mac build/launch/worker session. No current GitHub Actions matrix has completed while #137 lacks runners. The local foundation #130 and intelligent MVP #170 remain separate, open acceptance gates.

The first model-backed knowledge graph is an implemented **proposal and evidence** layer, not an independently proven answer engine. The full MVP still requires cited cross-domain chat, playable video moments, typed financial reconciliation and editable budget outputs, approved tasks, native evidence graph exploration (#383/#388) and measured provider selection.

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

Do not treat agent access as ordinary browser/UI access or grant tools by prompt instruction. Build #383 over existing evidence and relation records via a bounded read projection (#385), not an unnecessary second graph database. Validate renderer and real workloads under #384/#388.

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

The first-party SwiftUI application introduced in merged #145 now has a version-controlled Xcode Application target (#216) and follow-up build repairs (#217/#218/#220). All canonical storage, processing, inference and knowledge services remain in the .NET worker on private pipes; #107 is an independently gated external integration API, not a prerequisite for #130. The native app is **not yet hardware-accepted**; execute #215 then the full #130 fixture plan.

The [declarative GitHub plan](../../scripts/github/roadmap.json) now tracks #215 as a P1/M6 child of #64 and prerequisite for #130. Foundation work for user-owned collections (#146), quick capture (#113), watch recovery (#105) and original export (#166) is merged, but each issue retains its hardware/security acceptance as applicable. Project #3 fields and native dependencies require owner-side reconciliation after merge; modifying this file does not update the live private board.

## Owner-authored first-use collections — #146

Merged #147 added schema-v6 owner-authored collections, source membership, archive and collection-filtered browsing. Assignments are explicit source identities, not filesystem folders or model decisions. They survive source updates and coherent backups while active views exclude revoked sources. #146 remains open for actual current-app create/rename/archive/restart/restore acceptance and #137 cross-platform CI evidence.

Machine proposals belong to #115/#116 and do not silently change collection membership. The newer merged model/graph work #205–#212 shares the canonical provenance and review boundaries without making the initial no-model foundation dependent on inference.

## First-use quick note checkpoint (#113)

Typed or intentionally pasted notes are a P1/L0 first-use requirement, not a prerequisite to separately gated voice/browser integrations. Merged #148 implements quick note capture: the first-party app writes a bounded owner-authored Markdown file atomically into the **already-approved watched Inbox**, then commits the same exact bytes through the canonical original object and source-revision catalog. The existing text worker indexes that saved source; collection assignment and search are the same as for other approved Markdown files. No clipboard watcher, third-party service, URL fetch or agent model is involved.

The app provides an optional title, a text editor, Command-Return save, explicit acknowledgement and a retryable capture token while the process remains alive. A title is an authored Markdown heading, not an inferred label. Note file publication is owner-private on POSIX; root selection still obeys the approved watched-source boundary. Source files remain in the opted-in Inbox, and removing them from that folder does not automatically revoke preserved originals. The initial service uses `file` source identities rather than introducing a competing quick-note database.

After source-preserving capture and owner collections are usable, the separately controlled agentic continuation is #115: version- and digest-fenced proposed tags/topics/summaries from eligible verified extracted content, human review and abstention, followed by #143's bounded source-attributed context packs and #117–#119's explicit agent grants. This does not block note capture when no model is installed.

The parent workbench #108 and integrated release #130 remain open until an owner-facing end-to-end scenario passes on actual macOS hardware. This draft is not an executed UI build or green cross-platform CI.

## Subsequent knowledge-intelligence and connected-history lanes

The first local deliverable intentionally stops short of autonomous technical reasoning. The defining longer-term product objective is to turn authorized technical artifacts, ASM/decompilation, research, repositories and notes into a linked, evidence-grounded technical knowledge base, then answer questions that demand synthesis rather than filename lookup.

- **L4 — Technical evidence and investigation:** #149/#152–#154 choose and implement bounded technical extraction, explicit claims and contradictions; #150/#155/#156 evaluate limited investigation loops, citations and abstention. #29/#31/#33/#97 remain the common retrieval/model/chat and review components.
- **L5 — Connected history and interaction:** #151/#157 introduce typed provider events and replay-safe change history; #158 captures approved GitHub development history, #159 scoped local session transcripts, #162 investigates financial/social APIs and privacy; #160 is a separately approved tool proposal workflow, while #161 adds optional free local conversational voice.
- **Cost policy:** deterministic extraction/event classification before model inference; measured local Ollama/Qwen path first; Claude/Codex only through supported, explicitly consented invocation with input/cost disclosure; no assumption that user subscriptions include API use.
- **Security gate:** model output never grants authority. Provider accounts, local session histories and command execution require distinct, narrow, revocable consent. Sensitive financial and social account access remains a research decision, not a scheduled or implemented connector.

Do not create a redundant graph database, uncontrolled session monitor, provider marketplace or autonomous background agent. The native graph is an explicitly approved, measured first-party read projection with no new privileged writes (#383/#388). The owner can use L0 entirely offline with no AI provider; L4/L5 only exit when proven against real authorized cases.

 
## Reviewable enrichment checkpoint (#115)

Merged #165 established heading-derived, revision-fenced suggestions and owner accept/reject/correct history. Merged #204–#207 add deterministic text segmentation, bounded Ollama-derived topic/entity/relation proposals, quote-verification, a cross-source entity graph and explicit owner governance. #210 adds redaction-previewed, approved Codex/Claude CLI verification; #212 exposes Knowledge, Verification and Models workspaces. None of these agents can grant themselves tool permissions or overwrite canonical sources.

This is implemented proposal/graph infrastructure, **not** proof that a quoted assertion is true or that arbitrary sources are understood. #115/#116 remain open for measured quality, correction, rebuild/revocation and native first-party verification; #31/#172 own measured model escalation and #170 owns integrated answer/task acceptance.

## L0 source-original retrieval gate — #166

Merged #167 added user-selected original export; #192 fixed linked-destination aliases against the private catalog. The worker verifies current source identity and exact original bytes, stages into an owner-selected destination and refuses overwrite. It does not use the watched input path or grant a network/agent write.

#166 remains open for actual macOS Save dialog, symlink/TOCTOU, removal/revocation, cancellation and physical-volume acceptance. Merged implementation and the owner's prior reported Mac regression pass do not substitute for current native-app verification (#215/#130).

## First-use intake observability checkpoint (#105)

Merged #168 exposes watching/offline/degraded/paused states, last scan, bounded per-file errors and non-destructive pause/resume/retry controls. Merged #202 moved processing out of the watched intake scan and separated watcher responsibilities; #213 canonicalizes path aliases for stable source identity. These are implementation checkpoints, not complete fault-handling proof.

The #105/#130 exit still requires a real OS watcher, volume disconnection/reattachment, missed events/overflow, partial writes, restart, consent and recovery trials. No source disappearance should silently destroy its preserved canonical original.

## L0 event-confirmed source relocation — #105

Merged #169 preserves source identity only for observed in-scope file renames followed by stable SHA-256 and source-state verification, retaining provenance, collections and prior decisions. Unobserved moves and same-content copies are not guessed into one source; #213 also prevents duplicate logical identities from alternate symlink path spellings. Hardware rename, disconnect, missed-event and identity-reuse cases remain to be accepted under #105/#130.

## Cross-domain evidence

Merged #187 added timecoded caption cue evidence and a bounded, source-cited one-CSV financial summary; #188 added hypothetical owner-controlled spending targets; #193 added narrow digest-verified media header inspection. These are read-only evidence facilities, not finished media comprehension, chat playback, multi-source typed financial history or an approved editable budget workflow. #181/#184 and #177/#185 remain open, with #179/#170 owning independent cross-domain acceptance.
