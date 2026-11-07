# Product requirements and decision gates

This document separates product commitments from unverified assumptions. The initial requirements baseline is tracked by [#12](https://github.com/berniemackie97/Praxis/issues/12); outstanding deployment and privacy decisions remain tracked in [#50](https://github.com/berniemackie97/Praxis/issues/50).

## Confirmed product direction

| ID | Requirement | Verification |
| --- | --- | --- |
| R01 | Support macOS, Windows, Linux, Android, iOS, and browser clients over the product lifetime. | Each advertised platform must pass the release matrix in [platform verification](platform-verification.md). |
| R02 | Obsidian is the initial editor, not a dependency of canonical records or synchronization. | An independently implemented client can import, modify, synchronize, and export the same collection. |
| R03 | Users retain locally accessible, independently exportable notes and original attachments. | A full export/import round-trip verifies identifiers, content, metadata, references, and attachments. |
| R04 | Editing and capture must function without network access. | Edits survive termination while offline and reconcile safely after reconnection. |
| R05 | Concurrent device activity must not silently discard user-authored changes. | Deterministic tests cover simultaneous edits, stale writes, rename/edit and delete/edit conflicts. |
| R06 | Derived indexes and model outputs cannot be the sole copy of source content. | Rebuild succeeds from canonical sources without embedding-provider dependence. |
| R07 | Agent tools and connectors require explicit authorization, scoped credentials and recoverable audit history. | Unauthorized tool calls and sensitive data access are rejected in integration tests. |
| R08 | Complexity and ongoing cost require demonstrated benefit. | Add each infrastructure dependency only with a measurable benefit, failure model and maintenance cost. |
| R09 | Private content cannot leave the device without explicit trust/processing authorization. | Negative tests demonstrate no unauthorized upload, indexing, inference, connector transfer, or tool action. |

These requirements describe the destination. They do not imply that each platform and capability is implemented in the first release.

## Delivery profiles

### First usable milestone

- One owner can capture and edit Markdown locally on at least two authorized devices.
- A backend can exchange incremental changes without silent loss or requiring continuous connectivity.
- Conflict states are visible and recoverable; original attachments remain intact.
- A complete export and restore is demonstrable without the service being available.
- This milestone does not require remote model APIs, embeddings, agents, or a custom UI.

### Later milestones

- Real-device Obsidian integration on both mobile operating systems; user-visible sync status and repair.
- Capture from selected authorized sources, searchable provenance, and measurable retrieval evaluation.
- Explicitly approved automated actions with audit trails and safe recovery from failed jobs.
- A first-party client using the same canonical content and synchronization contracts.

## Platform and data boundaries

- **Target platforms:** macOS, Windows, Linux, Android, iOS/iPadOS, and the web.
- **Confirmed initial editor:** Obsidian, including its mobile application where compatible.
- **Initial deployment model:** undetermined. A modular monolith is a design default, not a commitment to one host.
- **Initial database and storage:** must be selected after record consistency, trust, and recovery semantics; avoid making a database export the only portable format.
- **Client execution model:** clients may run intermittently, be force-terminated, miss notifications, or be disconnected for extended periods.
- **Inference:** external providers must not be assumed authorized to receive private content.

## Decisions requiring owner input

| Decision | Needed before | Status |
| --- | --- | --- |
| Hosting: always-on home machine, VPS, or hybrid; how mobile devices reach it | Production authentication and sync transport | Open in #50 |
| Monthly recurring cost ceiling for hosting, storage, and model calls | Provider and deployment commitments | Open in #50 |
| Private-data policy: device-only, trusted server, or separately encrypted collections | Key custody, threat model and search architecture | Open in #50 |
| Data loss/recovery objectives and acceptable manual intervention | Revision retention, backups and account recovery | Open in #50 |
| Physical devices available for platform testing versus long-term platform targets | Real-device acceptance | Open in #50 |
| Initial capture sources and highest-value workflows | Ingestion and connector prioritization | Open in #50 |
| Single-user/household/multi-user needs and sharing boundaries | Account model and authorization | Open in #50 |

Do not record defaults as approved owner decisions. An ADR is accepted only when its tradeoffs and required evidence have been reviewed.

## Evidence required before the sync implementation

1. [Threat model](https://github.com/berniemackie97/Praxis/issues/13): plaintext trust, device authorization, keys, revocation and recovery.
2. [Canonical record specification](https://github.com/berniemackie97/Praxis/issues/14): identity, revisions, deletions, attachment references, lossless export.
3. [Sync specification](https://github.com/berniemackie97/Praxis/issues/17): change ordering, cursors, retry safety, clock skew and conflicts.
4. [Deployment decision](https://github.com/berniemackie97/Praxis/issues/50): representative device/workload profile, privacy model and chosen cost ceiling.

## Acceptance policy

Correctness and recoverability are release blockers. Latency, power consumption, network usage, cost, and storage growth require reproducible baselines and explicit thresholds before optimization. Publish no cross-platform or end-to-end encryption claims without matching tests and key-handling evidence.
