# Delivery roadmap

Praxis is delivered through verifiable capability gates, not arbitrary dates. GitHub [master roadmap #1](https://github.com/berniemackie97/Praxis/issues/1) indexes decisions and dependencies. The private [Praxis Roadmap Project](https://github.com/users/berniemackie97/projects/3) tracks execution.

## Milestone portfolio

| Gate | Epic | Features | Evidence required to exit |
| --- | --- | --- | --- |
| M0 — Foundation and decisions | [#2](https://github.com/berniemackie97/Praxis/issues/2) | Foundational work #10–#12; outstanding choices #50 and delivery tooling #67 | Verified CI, scoped review workflow, approved deployment/privacy/recovery requirements |
| M1 — Private and portable knowledge | [#3](https://github.com/berniemackie97/Praxis/issues/3) | [#53](https://github.com/berniemackie97/Praxis/issues/53) device trust; [#54](https://github.com/berniemackie97/Praxis/issues/54) portable content | Device authorization and recovery contract, note/attachment import-export and integrity evidence |
| M2 — Secure multi-device synchronization | [#4](https://github.com/berniemackie97/Praxis/issues/4) | [#55](https://github.com/berniemackie97/Praxis/issues/55) conflict protocol; [#56](https://github.com/berniemackie97/Praxis/issues/56) incremental exchange | Adversarial multi-client sync, safe retries, explicit conflicts, and authorization |
| M3 — Obsidian device integration | [#5](https://github.com/berniemackie97/Praxis/issues/5) | [#57](https://github.com/berniemackie97/Praxis/issues/57) vault interoperability; [#58](https://github.com/berniemackie97/Praxis/issues/58) mobile recovery | Real desktop/mobile device tests, offline resume, portable vault restore and conflict UX |
| M4 — Capture and retrieval | [#6](https://github.com/berniemackie97/Praxis/issues/6) | [#59](https://github.com/berniemackie97/Praxis/issues/59) source ingestion; [#60](https://github.com/berniemackie97/Praxis/issues/60) search quality | Idempotent ingestion, scoped provenance, full reindex and measured relevance |
| M5 — Authorized intelligence | [#7](https://github.com/berniemackie97/Praxis/issues/7) | [#61](https://github.com/berniemackie97/Praxis/issues/61) grounded assistance; [#62](https://github.com/berniemackie97/Praxis/issues/62) action execution | Explicit provider consent, citations, permission/approval tests, durable audited workflows |
| M6 — First-party clients | [#8](https://github.com/berniemackie97/Praxis/issues/8) | [#63](https://github.com/berniemackie97/Praxis/issues/63) client contracts; [#64](https://github.com/berniemackie97/Praxis/issues/64) independent UI | Edits, sync and recovery work without Obsidian on the advertised platforms |
| M7 — Operational readiness | [#9](https://github.com/berniemackie97/Praxis/issues/9) | [#65](https://github.com/berniemackie97/Praxis/issues/65) recoverability; [#66](https://github.com/berniemackie97/Praxis/issues/66) release resilience | Restore rehearsal, guarded deployment, migrations, supply-chain review and measured faults |

These stages define acceptance, not deadlines. The GitHub repository milestones and native issue relationships are provisioned by the reviewed [GitHub administration plan](../scripts/github/roadmap.json), using [setup instructions](project-setup.md).

## Cross-stage safety gates

Operations and security are not postponed until M7. Before private synchronization can be remotely enabled, the relevant parts of #15, #16, #19, #40, #41, and the architecture decision #50 must be implemented and verified. A later milestone's feature can therefore block an earlier stage's release.

The current service does **not** yet implement remote private-data storage, authentication, attachments, synchronization or inference. [ADR 0003](decisions/0003-processing-trust-boundaries.md) prevents treating planned security as deployed security.

## Delivery order

1. Complete the hosting, trust, key and recovery decisions in #50 and the threat model resolution in #13.
2. Extend the completed Markdown note archive #14 with binary attachment integrity #16.
3. Define the replay/conflict contract #17 and validate it with adversarial fixtures #21.
4. Implement identity, recovery, bounded incremental transport and multi-client synchronization.
5. Validate Obsidian adapters on real desktop/mobile devices before declaring M3 complete.
6. Layer ingestion, retrieval, permissioned actions and independent clients only after the appropriate data and authorization contracts are proven.

The [platform test matrix](platform-verification.md) and [requirements](requirements.md) distinguish target platforms from tested release support.

## Quality gates

An epic closes only after its feature outcomes are demonstrated and dependency links have no unsatisfied release blockers. A milestone closes when its acceptance evidence is present—not merely because all PRs are green or child issues have been administratively closed.

See [work item rules](tracking.md) for Definition of Ready/Done, classification, priority and WIP policy.
