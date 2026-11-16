# Delivery roadmap

Praxis is delivered through verifiable capability gates, not arbitrary dates. **Milestone numbers classify outcomes, not execution order.** A dependable local Praxis application is the immediate foundation; **the actual MVP is an integrated agent-powered, source-grounded Second Brain (#170)**, including native source-backed knowledge exploration, governed tools and cost-aware inference; Markdown/vault exchange is optional. Remote multi-device sync and unrelated account connectors remain separate. GitHub [master roadmap #1](https://github.com/berniemackie97/Praxis/issues/1) indexes decisions and dependencies. The private [Praxis Roadmap Project](https://github.com/users/berniemackie97/projects/3) tracks execution.

## Milestone portfolio

| Gate | Epic | Features | Evidence required to exit |
| --- | --- | --- | --- |
| M0 — Foundation and decisions | [#2](https://github.com/berniemackie97/Praxis/issues/2) | Foundational work #10–#12; outstanding choices #50 and delivery tooling #67 | Verified CI, scoped review workflow, approved deployment/privacy/recovery requirements |
| M1 — Private and portable knowledge | [#3](https://github.com/berniemackie97/Praxis/issues/3) | [#53](https://github.com/berniemackie97/Praxis/issues/53) device trust; [#54](https://github.com/berniemackie97/Praxis/issues/54) portable content | Device authorization and recovery contract, note/attachment import-export and integrity evidence |
| M2 — Secure multi-device synchronization | [#4](https://github.com/berniemackie97/Praxis/issues/4) | [#55](https://github.com/berniemackie97/Praxis/issues/55) conflict protocol; [#56](https://github.com/berniemackie97/Praxis/issues/56) incremental exchange | Adversarial multi-client sync, safe retries, explicit conflicts, and authorization |
| M3 — Native knowledge exploration and portability | [#5](https://github.com/berniemackie97/Praxis/issues/5) | [#383](https://github.com/berniemackie97/Praxis/issues/383) evidence graph; [#455](https://github.com/berniemackie97/Praxis/issues/455) dynamic conceptual regions; [#456](https://github.com/berniemackie97/Praxis/issues/456) Living Cortex; optional [#57](https://github.com/berniemackie97/Praxis/issues/57) Markdown/vault exchange | Current source/permission correctness, real owner-Mac graph navigation, accessible fallback, frame-time budgets and independent #388 acceptance |
| M4 — Capture and retrieval | [#6](https://github.com/berniemackie97/Praxis/issues/6) | [#59](https://github.com/berniemackie97/Praxis/issues/59) source ingestion (including [#485](https://github.com/berniemackie97/Praxis/issues/485) bounded ZIP child sources); [#60](https://github.com/berniemackie97/Praxis/issues/60) search quality; [#96](https://github.com/berniemackie97/Praxis/issues/96) voice/browser capture | Idempotent ingestion, scoped provenance, full reindex and measured relevance |
| M5 — Authorized intelligence | [#7](https://github.com/berniemackie97/Praxis/issues/7) | [#61](https://github.com/berniemackie97/Praxis/issues/61) grounded assistance/model routing; [#62](https://github.com/berniemackie97/Praxis/issues/62) permissions/tools; [#97](https://github.com/berniemackie97/Praxis/issues/97) reviewable organization; [#98](https://github.com/berniemackie97/Praxis/issues/98) agent/MCP; [#149](https://github.com/berniemackie97/Praxis/issues/149) technical knowledge; [#150](https://github.com/berniemackie97/Praxis/issues/150) investigations; [#151](https://github.com/berniemackie97/Praxis/issues/151) typed connector history | Source-cited technical answers, tested contradiction/abstention, scoped tool approvals, incremental provenance and durable audited workflows |
| M6 — First-party clients | [#8](https://github.com/berniemackie97/Praxis/issues/8) | [#63](https://github.com/berniemackie97/Praxis/issues/63) client contracts; [#64](https://github.com/berniemackie97/Praxis/issues/64) independent UI | Edits, sync and recovery work without Obsidian on the advertised platforms |
| M7 — Operational readiness | [#9](https://github.com/berniemackie97/Praxis/issues/9) | [#65](https://github.com/berniemackie97/Praxis/issues/65) recoverability; [#66](https://github.com/berniemackie97/Praxis/issues/66) release resilience | Restore rehearsal, guarded deployment, migrations, supply-chain review and measured faults |

These stages define acceptance, not deadlines. The GitHub repository milestones and native issue relationships are provisioned by the reviewed [GitHub administration plan](../scripts/github/roadmap.json), using [setup instructions](project-setup.md).

## Cross-stage safety gates

Operations and security are not postponed until M7. Before private synchronization can be remotely enabled, the relevant parts of #15, #16, #19, #40, #41, and the architecture decision #50 must be implemented and verified. A later milestone's feature can therefore block an earlier stage's release.

The current service does **not** yet implement remote private-data storage, authentication, attachments, synchronization or inference. [ADR 0003](decisions/0003-processing-trust-boundaries.md) prevents treating planned security as deployed security.

## Execution lanes (across capability milestones)

The validated [standalone delivery plan](delivery/standalone-mvp.md) and [product experience](product/second-brain-experience.md) define the current priority and user-visible acceptance:

1. **Local Praxis, no Obsidian:** merged implementation includes catalog #122/#124/#126, recoverable intake #101/#102, text extraction #103, indexed search #106 and initial trusted macOS UI #108. The caller trust decision #129 is complete; #105 watcher edge cases, #128 external-media recovery, #107 separately authenticated integration boundary, #113 direct-note and #146 collections acceptance remain independently tracked. **#130 is the required end-to-end first-use release proof**; it is open and not satisfied by the merged PRs alone.
2. **Fast capture:** quick thoughts #113, intentional microphone recording #109 and transcription #110, URL capture #104 and an explicitly paired browser extension #112/#111. These use the same provenance/ingestion model rather than independent stores.
3. **Knowledge depth and evaluation:** explicit folder watcher #105, safe rich-media metadata #114, ZIP child-source intake [#485](https://github.com/berniemackie97/Praxis/issues/485), reviewed suggestions #115, relationships #116 and retrieval measurements #29. An unsupported binary is preserved, never falsely described as understood.

   **ZIP archive intake gap (M4/P2, separate from #114):** The current generic readers store a normal ZIP original without producing independently searchable child documents. #485 must preserve the archive original and member-level parent/revision/path provenance, safely expand a bounded set of files through the existing canonical intake and knowledge processors, reconcile changes/revocation, reject archive attacks and demonstrate watched/manual Inbox search/citation on a nested Markdown corpus. It is not a new #170 MVP dependency or a replacement for portable vault archives #14/#16/#25.
4. **Agent/MCP compatibility:** contract and per-agent scope spike #117, local read-only #118, then separately authorized auditable writes #119. Untrusted content cannot authorize actions.

5. **Deep knowledge intelligence (#149/#150):** after a usable local knowledge MVP, evaluate real technical corpora #152; extract source/disassembly symbols and format evidence #153; reconcile grounded technical claims #154; use bounded investigations #155 and source-backed answer evaluations #156. Questions such as C3 parsing or DMap processing must produce technically supported explanations or abstain, not merely show matching files.
6. **Incremental connected history (#151):** versioned typed events #157 with deterministic provider metadata, then scoped GitHub change history #158 and approved local development/agent sessions #159. Sensitive bank/social feasibility #162 precedes any credentialed implementation. #30/#32/#35 remain consent and capability gates; tool proposals #160 and free offline conversational voice #161 are separately verified later capabilities.

Remote synchronization (M2), optional Obsidian/vault interoperability, advanced inference (M5) and broad platform/deployment readiness (M6/M7) remain separate release gates. M3 is the native knowledge explorer; #388 is its release acceptance gate. The work item [#50](https://github.com/berniemackie97/Praxis/issues/50) continues to block **remote** trust/hosting choices, not explicitly owner-local, network-disabled source capture.

### Immediate priority and success measures

The local foundation now contains a version-controlled native Xcode workbench, owner-authored collections and notes, automatic watched intake, verified original export and offline recovery. Merged #205–#212 additionally implement bounded Ollama enrichment, an owner-governed cross-source entity/relationship layer and separately approved Codex/Claude CLI verification. These provide evidence-bearing components, not a verified end-user assistant or release.

**Current checkpoint (November 15, 2026):** native development-app issue #215 and roadmap implementation issue #219 are closed. Owner-reported testing from a checkout labeled `main` passed **1,154 .NET tests and 66 native tests** and produced a packaged unsigned app; it does not establish complete L0 #130 or exact-head GitHub Actions CI. The real `CI` push run on merge `18fed5a` [43774017700](https://github.com/berniemackie97/Praxis/actions/runs/43774017700) still has Linux/macOS/Windows jobs **queued** under #137; successful Dependabot runs are not those tests. Keep #130, #137 and the integrated MVP #170 open.

After the L0 gate, the #170 product success test must independently demonstrate supported technical synthesis, source-linked in-chat video playback and reconciled editable monthly budgeting, along with approved tasks and permissioned knowledge projections. Existing read-only cue and CSV tools are not completion of those flows.

### Chat recovery gate

[#431](https://github.com/berniemackie97/Praxis/issues/431) remains the **P0 next active user-facing gate** under [#436](https://github.com/berniemackie97/Praxis/issues/436). Runtime fixes [#434](https://github.com/berniemackie97/Praxis/pull/434), history controls [#435](https://github.com/berniemackie97/Praxis/pull/435), model/effort pinning [#438](https://github.com/berniemackie97/Praxis/pull/438), bounded CLI context [#450](https://github.com/berniemackie97/Praxis/pull/450), tool-capability handling [#458](https://github.com/berniemackie97/Praxis/pull/458), local recall [#474](https://github.com/berniemackie97/Praxis/pull/474) and private recaps [#475](https://github.com/berniemackie97/Praxis/pull/475) have **merged**. None establishes the required [real Ollama/Codex/Claude provider smoke](delivery/chat-verification.md). Run that safe owner-Mac matrix with failure/timeout/cancellation/restart checks; fix only the evidenced defect, then advance #432/#433 context/usage/streaming. #137 cross-platform CI unblocking can progress as an independent delivery gate.

### Current knowledge and skills progress

- **One Brain:** #467/#468/#473 shipped a real-region projection and selectable Cellular Atlas, Knowledge Cosmos and first Living Cortex. The foundation is no longer a fixed type-bucket mock, but the owner has not approved the dynamic concept/classification accuracy and interaction/performance gates; #455/#456/#428/#388 remain open. #465/#466 added deterministic event-driven reconsolidation and a graph revision watermark, **not** complete reviewable model second looks or automatic animation (#463).
- **Skills:** merged #476 ships a text-only Skills workspace, approved versioned procedures, starter metadata lookup, local-only access and catalog migration v35 preserving v34 chat identity/recaps. #298/#339–#342 remain open for package/script sandbox tests, measured usage/evaluation, and owner GUI acceptance; no background privileged action is implied.
- **Current delivery order:** P0 real-provider Chat #431 and P0 graph responsiveness #428 require owner-Mac evidence; #137 3-OS CI is an independent blocker. When the Chat send/respond/stop gate is real, prioritize source truth and incremental conceptual correction (#463/#455) before additional decorative rendering. The #170 integrated release still needs technical/video/finance/task evaluations.

### Local foundation checkpoint — not the finished MVP

After #130 verification, an owner can open Praxis directly, explicitly add files/folders with mixed supported and unsupported formats, see provenance and processing errors, search supported extracted content, inspect source originals, retry after a restart, rebuild the index and verify restoration. Obsidian, cloud storage, an external AI model and a second device are **not requirements** for this local milestone.

### Later synchronization and release work

Finish the threat/host/key/recovery decisions (#13/#15/#50) and versioned conflict protocol (#17) before remotely reachable private sync (#18/#19). Do not advertise device or encryption support until matching test evidence exists. The M3 desktop archive/restore work remains available as an optional adapter, with unverified editor/mobile acceptance tracked in #22/#23/#25.


## True Praxis product MVP — integrated intelligent release gate (#170)

The owner's MVP is **not** satisfied by #130's capture, file browsing and keyword search. #130 is an enabling L0 acceptance checkpoint. The actual first release requires:

- **Interpret and relate across modalities:** #149/#152–#154 and #176/#177/#178 plus #115/#116 extract supported code/ASM concepts, time-aligned video moments and structured bills/income/transactions into versioned, provenance-rich, permissioned knowledge, without treating uncertain inferred claims as fact.
- **Reason and answer:** #29/#33/#150/#155/#156/#172/#179 prove evaluated retrieval/investigation across *three* domain gold sets, with exact text/symbol/time-range/record citations, honest uncertainty, no-answer and revocation behavior.
- **Efficient agents:** #143 provides task-scoped, hierarchically organized, versioned context packs and owner-reviewed root/nested `AGENTS.md` or tool-specific projections. Generated instructions never bypass consent or read unapproved data.
- **Local-first inference and escalation:** #31/#172 benchmark Ollama on actual local hardware, choose a measured retrieval+reasoning pipeline, and independently authorize supported Claude/Codex/Gemini/Grok/DeepSeek providers where available; enforce token/context/cost budgets, caching/invalidation, fallback policy and explicit disclosure.
- **Ask and do:** #33/#32/#34/#160/#173 support first-party persistent chat, planned bounded actions, tool discovery proposals, independent approval, progress, cancellation and auditable verification.
- **Native living knowledge:** #383/#388 require evidence-grounded, permissioned and accessible native exploration; #455 delivers evolving conceptual regions with re-evaluation under #463, while #456's cinematic anatomy remains a separate visual acceptance target. Optional #57/#171 Markdown/vault exchange is **not** an MVP prerequisite.
- **Real release gate:** #170 demonstrates these as one product on the owner's Mac: cited technical answers; directly playable tutorial segments in chat with full-video access; an editable and numerically verified monthly budget from authorized records; and one approved task. Failure, injection, data recovery, revocation and cost/latency evidence are mandatory.

The MVP includes feature [#176](https://github.com/berniemackie97/Praxis/issues/176) (multimodal and structured domain understanding), with [#177](https://github.com/berniemackie97/Praxis/issues/177) video clips, [#178](https://github.com/berniemackie97/Praxis/issues/178) financial budgeting and [#179](https://github.com/berniemackie97/Praxis/issues/179) cross-domain evaluation. Technical research is one representative use case, not the product boundary.

This is an **end-to-end vertical slice at production quality**, not a requirement to support all possible files, models, connected services or devices on day one. Foundational L0 #130 and real-provider Chat #431 are separate outstanding acceptance gates; the roadmap must not claim MVP completion based on development-app or individual PR test success.

## Second-brain depth and stewardship lane (post-MVP) — November 15, 2026

A review of six public "second brain" video walkthroughs found that Praxis' core is already ahead of them on provenance, permissions and local-first inference. They converge on a set of *stewardship* behaviours that are not part of the #170 gate: memory that is kept current rather than only accumulated, continuity across tools, recurring work, and one place to see what the system can reach. These are tracked as a separate lane so they never weaken or delay #130/#170.

| Capability | Issue | Depends on |
| --- | --- | --- |
| Living topic/entity evidence pages with reviewable diffs | [#442](https://github.com/berniemackie97/Praxis/issues/442) | #115, #116, #387 |
| Scheduled memory review of stale, conflicting and duplicate claims | [#445](https://github.com/berniemackie97/Praxis/issues/445) | #442, #316, #34 |
| Resume decisions and open work across chats and providers | [#440](https://github.com/berniemackie97/Praxis/issues/440) | #431, #432, #143 |
| Opt-in Codex/Claude Code lifecycle handoffs (hooks, not model-remembered tools) | [#441](https://github.com/berniemackie97/Praxis/issues/441) | #440, #118, #345 |
| Recurring workflows: preview, run, pause, receipts | [#443](https://github.com/berniemackie97/Praxis/issues/443) | #34, #339, #334 |
| One overview of connections, scopes, skills, routines and outcomes | [#447](https://github.com/berniemackie97/Praxis/issues/447) | #443, #318, #334 |
| Evidence-linked project workflow (idea → research → review → done) | [#446](https://github.com/berniemackie97/Praxis/issues/446) | #173, #146, #443 |
| Owner-hosted worker reachable from a paired phone (go/no-go spike) | [#444](https://github.com/berniemackie97/Praxis/issues/444) | #50, #19, #34 |
| Lexical-first retrieval, recency/supersession ranking and section pointers, benchmarked | [#451](https://github.com/berniemackie97/Praxis/issues/451) | #106, #316, #311 |
| Relationship-aware people profiles that change over time | [#452](https://github.com/berniemackie97/Praxis/issues/452) | #324, #316, #353 |

Already tracked elsewhere and deliberately not duplicated: interview-style onboarding and owner profile #353; teach-by-example skills #341; token/time savings for external agents #346; connector health and conformance #315/#317; screen-level computer use #338; Markdown export #171.

Rules for this lane:

- **Not an MVP gate.** Nothing here blocks #130 or #170. Promote an item only by an explicit owner decision recorded in the issue.
- **Derived, reviewable, reversible.** Pages, summaries, handoffs and profiles are derived from immutable originals, show their evidence and can be corrected, expired or forgotten; forgetting clears dependent caches. Imported text never grants tools or permissions.
- **Hooks over memory tools.** Capture that must always happen uses supported lifecycle events and exposes unsupported ones, rather than relying on a model to call a memory tool.
- **Measure before adding machinery.** Retrieval additions (#451) stay only where they beat the current path on the owner gold sets for quality, outdated-answer rate, tokens and latency.
- **Single schedulers and stores.** Routines use #34; workflows use existing project/collection records; no second task database and no unattended privileged writes.

`scripts/github/roadmap.json` carries this stewardship lane as L7. The plan now also tracks newer open issues #455/#456 under native evidence graph #383 and reconsolidation #463 under reviewable knowledge #97, with M3/M5 stages and guarded dependencies. Existing closed issues remain recorded as historical delivery evidence; project field synchronization is separate from issue body updates and must be verified on [Project #3](https://github.com/users/berniemackie97/projects/3).

## Quality gates

An epic closes only after its feature outcomes are demonstrated and dependency links have no unsatisfied release blockers. A milestone closes when its acceptance evidence is present—not merely because all PRs are green or child issues have been administratively closed.

See [work item rules](tracking.md) for Definition of Ready/Done, classification, priority and WIP policy.


## November 16, 2026 — owner-directed source acquisition and local specialist training

**Owner goal:** Ask Praxis to study an approved YouTube channel, playlist, university course, documentation site or source collection; have it discover available material, process what may lawfully and technically be acquired, form source-backed relationships, and report what remains unavailable or insufficiently understood. This is an auditable **collection workflow**, not a claim that the current app can autonomously watch any channel.

### Parallel delivery lane L8 — collection intelligence

[Feature #496](https://github.com/berniemackie97/Praxis/issues/496) (M5/P1 first slice under ingestion epic #288) adds collection-directed acquisition while **reusing** the existing ADR 0044 reader/triage/verified-understanding pipeline, canonical original/source identity, connector authorization, permissions and native Brain. Work is broken down into:

| Work | Outcome | Priority |
| --- | --- | --- |
| [#498](https://github.com/berniemackie97/Praxis/issues/498) | Bound and identify channel/course/site collection manifests without blind crawl | P1 |
| [#499](https://github.com/berniemackie97/Praxis/issues/499) | Authorized HTML/PDF and permitted media/caption acquisition with exact original receipts, SSRF and rights protections | P1 |
| [#500](https://github.com/berniemackie97/Praxis/issues/500) | Durable resumable collection jobs with per-item status, quotas, approval and revocation | P1 |
| [#501](https://github.com/berniemackie97/Praxis/issues/501) | Source-backed course/playlist/module/page/time relationships and truthful evidence coverage | P2 |
| [#502](https://github.com/berniemackie97/Praxis/issues/502) | Approved research/acquisition tool contracts and versioned, owner-reviewed `study-source-collection` skill | P2 |
| [#503](https://github.com/berniemackie97/Praxis/issues/503) | Measured end-to-end quality, local/remote cost, security and restart acceptance on a representative course | P1 |

**Existing boundaries:** #104/#111 cover URL/browser capture, #294/#317–#319 connector security and owner rules, #329 audio/video caption/transcription work, #296/#177 playback semantics, #485 ZIP member sources, #173/#334 agent task approvals, and #298 Skills v1. The new work must not duplicate any of these, grant tool rights from imported text, or assume that arbitrary YouTube captions/media are available for download. A transcript-only import is not proof of understanding visual demonstrations. Full autonomous source acquisition is not a new #170 MVP blocker.

Initial implementation sequence: audit and define #498/#499 contracts against live code; implement a small permitted course subset; add #500 durable coordination; link #501 verified source hierarchies and #502 chat/skill interaction; execute #503 owner-Mac acceptance before broadening to whole channels or periodic refresh. Broad #494 user-defined agent organizations remain separate post-MVP work, and should reuse these tools once independently approved.

### Small-model evidence specialist — separate research slice

[Task #504](https://github.com/berniemackie97/Praxis/issues/504) (M5/P1 research under model-family feature #490) defines **Praxis Signal 1.5B**, initially evaluating query intent, relevance, evidence sufficiency and contradiction/abstention. Preserve G1 checkpoint lineage (not a promoted model); start from pinned foundation and reviewed labels. Benchmark against prompt-only 1.5B, existing embedding models, Qwen3-Reranker-0.6B and deterministic search. Measure held-out source-family generalization, false sufficiency, citations, latency, cost and privacy before promotion (#492). Brain memory changes do not directly alter model weights; training rights are opt-in (#491).

**Execution reality:** Planning issues and this versioned branch are not deployed capabilities. Project #3 Stage/Priority/Status, native parent relationships and actual source/plugin availability must be reconciled and verified separately. The P0 existing chat acceptance (#431) and foundation/MVP evidence gates (#130/#170) remain unchanged.
