# Delivery roadmap

Praxis is delivered through verifiable capability gates, not arbitrary dates. **Milestone numbers classify outcomes, not execution order.** A small local Praxis application now takes delivery priority over unfinished remote sync and optional Obsidian integration. GitHub [master roadmap #1](https://github.com/berniemackie97/Praxis/issues/1) indexes decisions and dependencies. The private [Praxis Roadmap Project](https://github.com/users/berniemackie97/projects/3) tracks execution.

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

Merged PRs #144/#145/#147/#148 implement automatic intake, initial macOS workbench, owner collections and quick notes. Their integration does not close the full #130 acceptance gate: confirm fresh-install/GUI, mixed files, restart/recovery, denied access and supported search with actual results before claiming a usable local release. #146 remains open for unverified acceptance despite its merged implementation.

The **product success test** after L0 goes beyond document retrieval: ask a challenging question spanning disassembly, source code and notes; Praxis must reconstruct the supported answer with attributable evidence, highlight contradictions and gaps, and avoid claiming unsupported binary comprehension. This is planned in L4, not a new L0 release blocker.

### First useful milestone (local)

After #130 verification, an owner can open Praxis directly, explicitly add files/folders with mixed supported and unsupported formats, see provenance and processing errors, search supported extracted content, inspect source originals, retry after a restart, rebuild the index and verify restoration. Obsidian, cloud storage, an external AI model and a second device are **not requirements** for this local milestone.

### Later synchronization and release work

Finish the threat/host/key/recovery decisions (#13/#15/#50) and versioned conflict protocol (#17) before remotely reachable private sync (#18/#19). Do not advertise device or encryption support until matching test evidence exists. The M3 desktop archive/restore work remains available as an optional adapter, with unverified editor/mobile acceptance tracked in #22/#23/#25.


## Quality gates

An epic closes only after its feature outcomes are demonstrated and dependency links have no unsatisfied release blockers. A milestone closes when its acceptance evidence is present—not merely because all PRs are green or child issues have been administratively closed.

See [work item rules](tracking.md) for Definition of Ready/Done, classification, priority and WIP policy.
