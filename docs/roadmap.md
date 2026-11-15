# Delivery roadmap

Praxis is delivered through verifiable capability gates, not arbitrary dates. **Milestone numbers classify outcomes, not execution order.** A dependable local Praxis application is the immediate foundation; **the actual MVP is an integrated agent-powered, source-grounded Second Brain (#170)**, including Obsidian-accessible derived knowledge, governed tools and cost-aware inference. Remote multi-device sync and unrelated account connectors remain separate. GitHub [master roadmap #1](https://github.com/berniemackie97/Praxis/issues/1) indexes decisions and dependencies. The private [Praxis Roadmap Project](https://github.com/users/berniemackie97/projects/3) tracks execution.

## Milestone portfolio

| Gate | Epic | Features | Evidence required to exit |
| --- | --- | --- | --- |
| M0 — Foundation and decisions | [#2](https://github.com/berniemackie97/Praxis/issues/2) | Foundational work #10–#12; outstanding choices #50 and delivery tooling #67 | Verified CI, scoped review workflow, approved deployment/privacy/recovery requirements |
| M1 — Private and portable knowledge | [#3](https://github.com/berniemackie97/Praxis/issues/3) | [#53](https://github.com/berniemackie97/Praxis/issues/53) device trust; [#54](https://github.com/berniemackie97/Praxis/issues/54) portable content | Device authorization and recovery contract, note/attachment import-export and integrity evidence |
| M2 — Secure multi-device synchronization | [#4](https://github.com/berniemackie97/Praxis/issues/4) | [#55](https://github.com/berniemackie97/Praxis/issues/55) conflict protocol; [#56](https://github.com/berniemackie97/Praxis/issues/56) incremental exchange | Adversarial multi-client sync, safe retries, explicit conflicts, and authorization |
| M3 — Obsidian device integration | [#5](https://github.com/berniemackie97/Praxis/issues/5) | [#57](https://github.com/berniemackie97/Praxis/issues/57) vault interoperability; [#58](https://github.com/berniemackie97/Praxis/issues/58) mobile recovery | Real desktop/mobile device tests, offline resume, portable vault restore and conflict UX |
| M4 — Capture and retrieval | [#6](https://github.com/berniemackie97/Praxis/issues/6) | [#59](https://github.com/berniemackie97/Praxis/issues/59) source ingestion; [#60](https://github.com/berniemackie97/Praxis/issues/60) search quality; [#96](https://github.com/berniemackie97/Praxis/issues/96) voice/browser capture | Idempotent ingestion, scoped provenance, full reindex and measured relevance |
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
3. **Knowledge depth and evaluation:** explicit folder watcher #105, safe rich-media metadata #114, reviewed suggestions #115, relationships #116 and retrieval measurements #29. An unsupported binary is preserved, never falsely described as understood.
4. **Agent/MCP compatibility:** contract and per-agent scope spike #117, local read-only #118, then separately authorized auditable writes #119. Untrusted content cannot authorize actions.

5. **Deep knowledge intelligence (#149/#150):** after a usable local knowledge MVP, evaluate real technical corpora #152; extract source/disassembly symbols and format evidence #153; reconcile grounded technical claims #154; use bounded investigations #155 and source-backed answer evaluations #156. Questions such as C3 parsing or DMap processing must produce technically supported explanations or abstain, not merely show matching files.
6. **Incremental connected history (#151):** versioned typed events #157 with deterministic provider metadata, then scoped GitHub change history #158 and approved local development/agent sessions #159. Sensitive bank/social feasibility #162 precedes any credentialed implementation. #30/#32/#35 remain consent and capability gates; tool proposals #160 and free offline conversational voice #161 are separately verified later capabilities.

Remote synchronization (M2), Obsidian desktop/mobile parity (M3), advanced inference (M5) and broad platform/deployment readiness (M6/M7) remain separate release gates. The work item [#50](https://github.com/berniemackie97/Praxis/issues/50) continues to block **remote** trust/hosting choices, not explicitly owner-local, network-disabled source capture.

### Immediate priority and success measures

The local foundation now contains a version-controlled native Xcode workbench, owner-authored collections and notes, automatic watched intake, verified original export and offline recovery. Merged #205–#212 additionally implement bounded Ollama enrichment, an owner-governed cross-source entity/relationship layer and separately approved Codex/Claude CLI verification. These provide evidence-bearing components, not a verified end-user assistant or release.

**Immediate P1 dependency:** verify current Xcode build, native launch and private .NET worker in #215 after merged #216–#220; then execute the mixed-source, offline/restart/security acceptance #130. GitHub Actions #137 is waiting on owner-deferred runners rather than producing green tests. #219 owns the P2 documentation/project-plan reconciliation. No hardware or cross-platform passing result is inferred from PR merge.

After the L0 gate, the #170 product success test must independently demonstrate supported technical synthesis, source-linked in-chat video playback and reconciled editable monthly budgeting, along with approved tasks and permissioned knowledge projections. Existing read-only cue and CSV tools are not completion of those flows.

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
- **Obsidian companion:** #57/#171 publish owner-approved, linked and provenance-grounded Markdown views into a selected vault without destructive conflicts or a competing canonical store.
- **Real release gate:** #170 demonstrates these as one product on the owner's Mac: cited technical answers; directly playable tutorial segments in chat with full-video access; an editable and numerically verified monthly budget from authorized records; and one approved task. Failure, injection, data recovery, revocation and cost/latency evidence are mandatory.

The MVP includes feature [#176](https://github.com/berniemackie97/Praxis/issues/176) (multimodal and structured domain understanding), with [#177](https://github.com/berniemackie97/Praxis/issues/177) video clips, [#178](https://github.com/berniemackie97/Praxis/issues/178) financial budgeting and [#179](https://github.com/berniemackie97/Praxis/issues/179) cross-domain evaluation. Technical research is one representative use case, not the product boundary.

This is an **end-to-end vertical slice at production quality**, not a requirement to support all possible files, models, connected services or devices on day one. Foundational L0 remains the next blocking engineering gate, but the roadmap must not claim MVP completion there.

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

`scripts/github/roadmap.json` carries this lane as delivery lane L7, together with the open issues from #137 and #268 through #452 that were previously untracked (graph exploration #416–#428, Chat #431–#449, verification #427). Closed issues, and #421, whose `[Security]` title prefix is not an accepted kind, remain outside the plan.

## Quality gates

An epic closes only after its feature outcomes are demonstrated and dependency links have no unsatisfied release blockers. A milestone closes when its acceptance evidence is present—not merely because all PRs are green or child issues have been administratively closed.

See [work item rules](tracking.md) for Definition of Ready/Done, classification, priority and WIP policy.
