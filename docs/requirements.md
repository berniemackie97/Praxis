# Product requirements and decision gates

This document separates product commitments from unverified assumptions. The initial requirements baseline is tracked by [#12](https://github.com/berniemackie97/Praxis/issues/12); outstanding deployment and privacy decisions remain tracked in [#50](https://github.com/berniemackie97/Praxis/issues/50).

## Confirmed product direction

| ID | Requirement | Verification |
| --- | --- | --- |
| R01 | Support macOS, Windows, Linux, Android, iOS, and browser clients over the product lifetime. | Each advertised platform must pass the release matrix in [platform verification](platform-verification.md). |
| R02 | Obsidian is an optional editor and archive adapter, not a prerequisite for canonical records, direct capture, retrieval or the first useful Praxis interface. | The owner can import, inspect, search and recover local content through a first-party interface without installing Obsidian; later interoperability and synchronization have separate verification gates. |
| R03 | Users retain locally accessible, independently exportable notes and original attachments. | A full export/import round-trip verifies identifiers, content, metadata, references, and attachments. |
| R04 | Editing and capture must function without network access. | Edits survive termination while offline and reconcile safely after reconnection. |
| R05 | Concurrent device activity must not silently discard user-authored changes. | Deterministic tests cover simultaneous edits, stale writes, rename/edit and delete/edit conflicts. |
| R06 | Derived indexes and model outputs cannot be the sole copy of source content. | Rebuild succeeds from canonical sources without embedding-provider dependence. |
| R07 | Agent tools and connectors require explicit authorization, scoped credentials and recoverable audit history. | Unauthorized tool calls and sensitive data access are rejected in integration tests. |
| R08 | Complexity and ongoing cost require demonstrated benefit. | Add each infrastructure dependency only with a measurable benefit, failure model and maintenance cost. |
| R09 | Private content cannot leave the device without explicit trust/processing authorization. | Negative tests demonstrate no unauthorized upload, remote inference, connector transfer, or tool action. |
| R10 | The initial useful Praxis app must not depend on Obsidian or continuous network access. | From a clean device session, import, find and restore local items with no Obsidian installation or remote service. |
| R11 | Source originals and derived knowledge are distinct, auditable and independently recoverable. | Exact original byte hashes, provenance, extract/processor versions, proposed tags and full reindex survive restart and source updates. |
| R12 | Dictation and browser capture are intentional, permissioned and loss-aware. | Mic activation/stop, browser origins, pairing, offline queue and transcript/source attribution pass negative and restart tests. |
| R13 | Agents use explicit scoped MCP capabilities rather than implicit filesystem access. | Read/fetch and separately approved mutations enforce per-agent grants, source scope and revocation independent of prompt content. |
| R14 | The first intelligent MVP spans **multiple knowledge domains**, not only code or technical notes: timecoded owner-approved media and structured personal finance are required evaluation workflows. | Integrated release gate #170 passes independently scored technical, video and monthly budget tasks with source/version evidence, abstention and privacy cases. |
| R15 | A supported video question returns the **relevant in-chat playable clip** and an authorized route to its full original, not merely a search result or unverified timestamp. | Named source, revision/digest, original transcript evidence and exact time range survive restarts, source changes and revocation; macOS playback is actually demonstrated. |
| R16 | Given approved income, bills and transactions, Praxis generates a grounded, reviewable **monthly budget and proposed cuts** without inventing costs or moving funds. | Typed deterministic reconciliation and amount arithmetic, source-row citations, owner assumptions and missing-data warnings agree with a gold fixture; any saved output requires explicit tool approval. |

These requirements describe the destination. They do not imply that each platform and capability is implemented in the first release.

## Delivery profiles

### First useful milestone — standalone local Praxis

- One owner opens a first-party Praxis interface on a supported desktop without Obsidian, an account, network access or any model provider.
- The owner explicitly imports a mixed local file/folder corpus. Original bytes, distinct source identities, provenance, processing status and supported extracted text survive restart, cancellation and retry without duplicates.
- The owner searches supported text, opens source-grounded results and inspects originals; unprocessed/unsupported media is visible and not invented as searchable content.
- A derived search index can be fully rebuilt; source deletion/revocation and content updates are reflected without stale unauthorized results.
- Current originals and metadata can be exported/restored with verified bytes and explicit documented metadata limits. An unrelated process or hostile page cannot read local private content.
- Capture, extraction and search require no upload or remote inference. A separately user-triggered URL fetch is an explicit network action, not a background permission.

### Follow-on capture and intelligence

- Quick-note and microphone dictation workflows preserve recordings and editable transcript revisions with explicit consent.
- Browser-extension URL/page/selection capture requires deliberate user action, narrow permissions, local pairing and offline-safe retries.
- Reviewable tags, summaries, topics and cross-source project relationships preserve source provenance and user corrections.
- MCP agents obtain scoped read-only search/fetch first; capture or modifications require separate authorization, durable receipts and approvals.
- Source-specific semantic analysis and grounded assistance are evaluated against actual data quality, not presumed to work for every media/game file.

### Independent future release milestones

- Authorized multi-device sync, conflicts, account/device recovery and remote access are independently verified under M2/#50; the local-first milestone never implies those features work.
- Real-device Obsidian integration on desktop/mobile remains optional M3 acceptance.
- Richer connectors, cross-platform first-party clients, approved model providers and operational recovery have separate feature gates.

## Platform and data boundaries

- **Target platforms:** macOS, Windows, Linux, Android, iOS/iPadOS, and the web.
- **First usable interaction:** a standalone Praxis local interface. Obsidian is an optional editor/capture adapter, including its mobile application where compatible.
- **Initial deployment model:** undetermined. A modular monolith is a design default, not a commitment to one host.
- **Owner-local catalog/storage:** SQLite schema and content-addressed original publication were implemented in #122/#124. Stable repeat-import receipts and revision transitions remain #126; coherent catalog-plus-original restore remains #128. Portable export must remain available independent of SQLite internals; do not treat the current schema as proof of recovery.
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
| Initial capture sources and highest-value workflows | Ingestion and connector prioritization | The owner confirmed a standalone local Inbox, dictation, browser capture and future MCP. The initial catalog decision #99 is closed; precise additional capture formats, connector scopes and consent remain #30, and remote processing/hosting remain #50 |
| Single-user/household/multi-user needs and sharing boundaries | Account model and authorization | Open in #50 |

Do not record defaults as approved owner decisions. An ADR is accepted only when its tradeoffs and required evidence have been reviewed.

## Evidence required before the sync implementation

1. [Threat model](https://github.com/berniemackie97/Praxis/issues/13): plaintext trust, device authorization, keys, revocation and recovery.
2. [Canonical record specification](https://github.com/berniemackie97/Praxis/issues/14): identity, revisions, deletions, attachment references, lossless export.
3. [Sync specification](https://github.com/berniemackie97/Praxis/issues/17): change ordering, cursors, retry safety, clock skew and conflicts.
4. [Deployment decision](https://github.com/berniemackie97/Praxis/issues/50): representative device/workload profile, privacy model and chosen cost ceiling.

## Acceptance policy

Correctness and recoverability are release blockers. Latency, power consumption, network usage, cost, and storage growth require reproducible baselines and explicit thresholds before optimization. Publish no cross-platform or end-to-end encryption claims without matching tests and key-handling evidence.
