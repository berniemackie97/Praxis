# ADR 0037: Deployment, processing trust, device identity and recovery

**Status:** Proposed for owner approval in [#50](https://github.com/berniemackie97/Praxis/issues/50). **Nothing in this proposal approves a hosting topology, encryption mode, account model, recovery target or revised release scope.**
**Decision authority:** Praxis owner. **Implementation owners:** #13, #15, #18, #19, #40, #41 and #46.
**Relationship:** Supplements [ADR 0003](0003-processing-trust-boundaries.md) (processing and disclosure), [ADR 0006](0006-versioned-sync-protocol.md) (sync), [ADR 0018](0018-coherent-local-backup.md) (local snapshots) and [ADR 0021](0021-owner-local-caller-security.md) (local caller authorization). Does not weaken or supersede them.

## Audited baseline

Praxis has a first-party SwiftUI macOS application with a .NET worker, a local catalog, immutable originals, revisioned derived content, local search, local Ollama inference and separately approved external subscription-CLI verification. The catalog, originals, derived content, indexes and coherent local snapshot are **plaintext**. Their verified hashes and strict file modes do not constitute application-layer encryption, hostile-host integrity protection or recoverability after loss of encryption keys. Real removable-drive and unexpected power-loss recovery acceptance remain open.

[ADR 0006](0006-versioned-sync-protocol.md) is a versioned, test-fixture-backed synchronization contract, **not** an authenticated durable remote implementation. Remote device enrollment, authorization, rotation, revocation and recovery are unimplemented (#15); authenticated bounded transport, operational backups and secure deployment are separate incomplete tasks (#19, #40, #41). The current HTTP API is liveness-only and grants no permission to expose private content. The local first-party worker transport must be separately validated against ADR 0021's protected-IPC/peer-identity expectations; spawning a helper is not proof of isolation.

The existing owner-approved local caller model permits neither household accounts, remote sync, at-rest/E2E encryption claims nor automatic cloud model disclosure. The owner has confirmed an independent local Inbox, attributed knowledge retrieval, first-party UI and governed local-first inference; the seven choices below are **not** established by those product requirements. Existing remote synchronization remains a real planned capability, not silently removed or postponed by this document.

## Required owner decisions

All entries need an explicit selection, date, approved threat/cost tradeoffs and measurable exit condition. **Unselected** never means an implicit secure or insecure default.

| ID | Decision | Required recorded evidence | Approval |
| --- | --- | --- | --- |
| D1 — Supported devices | Physical test inventory, platform and minimum OS versions; which desktop/mobile systems must ship | Real hardware/OS/filesystem matrix, credentials and secure-storage capabilities, unsupported-platform behavior | Unselected |
| D2 — Hosting and reachability | Who operates a sync host, where it runs, public/VPN/LAN reachability, TLS and ingress, maintenance and update authority, whether remote sync is required in the first release | Deployment and network diagrams, bootstrap/update/rollback runbook, host/operator trust boundary | Unselected |
| D3 — Budget and workload | Recurring spending ceiling; retained corpus, total and largest attachments, daily change volume, device and user count, concurrency and expected growth | Approved cost and resource budgets with a representative reproducible fixture and measured capacity targets | Unselected |
| D4 — Principal scope | One owner, separately isolated household members or multiple accounts; sharing, delegated rights and collection custody | Principal/collection permission matrix and deny-by-default tests across principals | Unselected |
| D5 — Recovery objectives | Maximum committed-data loss (RPO), restore time (RTO), acceptable outage/offline interval, retention and independent backup sites, lost-device and all-keys-lost behavior | Exact disaster scenarios, recovery authority, key/backup custody and target measured restore rehearsal | Unselected |
| D6 — Processing confidentiality | Trusted plaintext service, client-held encrypted collections, or strictly isolated per-collection classes; remote unattended indexing/inference requirements | Explicit data/key/metadata flows, plaintext locations, index/backup visibility and approved disclosure contract | Unselected |
| D7 — First-use evidence | Ranked technical/captioned-video/structured-financial or other owner-selected source-to-answer/task scenarios, evaluation corpus and measurable acceptance | Versioned fixtures, ground truth, negative cases, grounded/abstention rules and performance limits | Unselected |

A development Mac is not a certified second-device test matrix. A personal LLM subscription does not constitute an API entitlement, zero-cost hosted deployment, or approval to transmit private source excerpts.

## Processing design alternatives

**A — Trusted processing service.** The service has access to permitted plaintext and can index, search and execute specifically approved jobs remotely. It requires explicit host/operator trust, service key custody, least-privilege authorization, retained derived-data controls, authenticated encrypted backups, key-rotation and incident procedures. TLS and volume encryption do **not** make the service end-to-end encrypted. Compromised operators and hosts may access plaintext.

**B — Client-held encrypted collections.** An authenticated remote service stores opaque authenticated ciphertext and the minimum protocol-visible metadata needed for ordering, receipts, quotas and synchronization. The server does not hold content-decrypting keys. Local authorized clients perform content-based indexing, retrieval and inference; unattended server-side plaintext work is impossible without a separate explicitly authorized processing boundary. The service cannot validate hidden plaintext filenames, links or source conflicts and must not claim to. Key enrollment, cryptographic device wrapping, rotation, recovery and all-keys-loss consequences are release-critical. The service still sees some request, timing, size, account and device metadata.

**C — Explicit per-collection processing classes.** Collections declare independently enforced visibility and key custody. Every index, queue, audit record, cache, model call, backup, export and replication path must carry and enforce this state. Reclassification requires an approved migration, protection of old copies and audit of all derived material. This introduces substantial cross-mode complexity and is unjustified without a concrete workflow showing that neither A nor B suffices.

**Provisional engineering assessment, not approval:** the local-first computation architecture makes B a plausible privacy-preserving remote-sync design. That advantage is real only if the owner accepts client-side processing and the independently tested recovery limitations. If unattended server-side processing of private sources is essential, the owner must select an authorized plaintext-processing mode; pretending it works over server-opaque ciphertext would be incorrect. No crypto scaffolding, silent fallback or feature flag is authorized before selection.

## Non-negotiable implementation contracts

1. **Identity and authority.** Enrollment is an explicit approved binding between owner, device and permitted collections. Tokens have bounded scope, audience and lifetime; renewal, compromise, revocation and offline-device behavior are specified. Device IDs, source IDs, operation UUIDs, request keys and cursors are identifiers—not credentials. Review standards-based OpenID Connect with system-browser authorization-code/PKCE where applicable; do not build an unneeded password/OAuth server. An authenticated owner account alone is not device enrollment.
2. **Key lifecycle.** If B is selected, document vetted authenticated-encryption and key-wrapping libraries/formats, secure random generation, nonces, device key custody, envelope/version migrations, lost-device re-enrollment, backup recovery material and irreversible loss of all decryption keys. If A is selected, document decrypting service principal, operator access, server keys and independent key/backup recovery. No custom cryptographic primitive or unreviewed recovery backdoor.
3. **Disclosure boundary.** Classify originals, metadata, text excerpts, derived records, embeddings, model prompts, diagnostics, temporary files and archives separately. Per-source/user authorization and owner-approved external disclosure apply to each path; no cloud fallback or unaudited indexing. Revocation and deletion must retract access to derived indexes, caches, excerpts and agent results while documenting unavoidable copies already held by offline devices.
4. **Network and protocol.** No unauthenticated private-data route. Require selected host trust, secure transport, per-record/collection authorization, replay and request-body rejection, strict input size/concurrency/rate/deadline budgets and nonsecret telemetry. Preserve ADR 0006's atomic expected-revision writes, device-bound idempotency receipts, tombstones, explicit conflicts, durable pull cursors and safe reset. Independently prove crash/restart and lost-ack convergence with two persisted clients.
5. **Backup and rollback.** Define a durable, encrypted and tamper-detectable independent backup for the complete catalog, all referenced originals, retained revision/event history, grant state and recovery keys as appropriate. Verify restore into a new environment without silently overwriting live data; exercise corruption, missing media, bad credentials, expired cursors, interrupted import, key loss and incompatible upgrades. Existing SHA-256 manifests are not hostile-host authenticity guarantees; owner-local plaintext snapshots are not encrypted offsite backups.
6. **Operational rigor.** Specify one reproducible supported deployment, least-privilege service identities, protected secret stores, clean bootstrap, update, rollback, secure migration, incident/revocation procedures, bounded logging and disk/memory/network/CPU quotas. Test overload and failed external dependencies without dropping acknowledged data or changing privacy mode.
7. **Observable budgets.** Commit measurable throughput, size, device count, concurrency, latency percentiles, RPO, RTO and retention only after D1–D7 workloads/targets are approved. Record environment, software revision, filesystem, fault schedule, source corpus and actual measured results; do not invent capacity claims or infer device acceptance from unit tests.

## Engineering acceptance matrix

| Gate | Issue owners | Exit evidence |
| --- | --- | --- |
| Trust decision and threat model | #50, #13; compatibility with closed #14/#17 | D1–D7 dated owner choices, processing/key diagrams, cross-collection/access threats, explicit residual risks, validated compatibility with interchange and sync contracts |
| Remote identity | #15 | Enrolled/unenrolled/expired/revoked/stolen/replayed devices; cross-principal and cross-collection denials; verified recovery and process-restart behavior |
| Durable authenticated sync | #18, #19, #20, #21 | Two independently persisted clients, retained pending changes, ordered cursor/rebootstrap, response-loss idempotency, attachment consistency and adversarial conflicts |
| Recovery and release safety | #16, #40, #46 | Encrypted authenticated backup/restore of content and required secrets, chosen RPO/RTO verified by fault rehearsal on selected devices/filesystems, no silent loss on migration/rollback |
| Deployment and capacity | #41, #47 | Repeatable hardened host; positive and denial tests; measured bounds, clean diagnostics, safe key rotation, resource exhaustion and rollback |
| Grounded first-use workflows | #28, #29, #31, #32, #33, #170, #179 | Representative versioned source fixtures, correct citations/timecodes/amounts or abstention, denied prompt injection, permitted action receipts, redaction, revocation and disclosure-cost evidence |

No unexecuted CI, memory-only protocol fixture, snapshot source audit or unverified hardware description counts as runtime acceptance. [#137](https://github.com/berniemackie97/Praxis/issues/137) still blocks executed cross-platform Actions verification, and [#215](https://github.com/berniemackie97/Praxis/issues/215) records outstanding owner-Mac application acceptance.

## Decision acceptance procedure and restrictions

Before this ADR can become **Accepted**, each D1–D7 row must contain the owner's explicit choice and review date, chosen/rejected alternatives and precise acceptance targets; the threat model must reflect the chosen plaintext/key paths; and #13/#14/#15/#17/#18/#19/#40/#41 must be reconciled against the actual product boundary. Amend [roadmap.json](../../scripts/github/roadmap.json) only if that audit demonstrates a *real* dependency change, then verify and apply it through the authorized Project #3 reconciler after review. Do not remove dependencies for schedule convenience.

**Until acceptance and completed implementation gates, remotely reachable private sync, unauthorized agent actions and unapproved remote inference remain disabled.** Acceptance of the decision record is not proof that any remote security control has shipped; #15, #19, #40 and #41 remain open until their implementation and verification criteria pass.

## External security reference baselines

- [OWASP ASVS 5.0.0](https://asvs.owasp.org/) for selecting verifiable web-service security controls, not a claim of certification.
- [NIST SP 800-63-4](https://csrc.nist.gov/pubs/sp/800/63/4/final) and [SP 800-63B-4](https://csrc.nist.gov/pubs/sp/800/63/b/4/final) as current reference material for remote authentication and authenticator lifecycle if those capabilities are selected.
- [Praxis threat model](../security/threat-model.md) and [adversarial sync scenarios](../testing/sync-adversarial.md) for repository-specific failure and abuse cases.
