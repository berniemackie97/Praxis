# Architecture baseline

Praxis is a client-independent knowledge and automation platform. Obsidian is an optional supported editor adapter, **not** a requirement for first-party capture, retrieval, storage or use. The first useful release is an owner-local Praxis Inbox/Library/Search application; full multi-device and hosted trust profiles are later gated capabilities.

## Invariants

1. Personal information is independently exportable in documented, portable formats.
2. Local editing remains possible without network access.
3. Synchronization retries cannot duplicate effects or overwrite conflicting edits silently.
4. Concurrent writes, renames, deletions, and stale clients require explicit conflict semantics.
5. Search indexes and embeddings can be rebuilt from canonical records.
6. Agents have no implicit write or external-action permissions.
7. Ingested data retains provenance, access scope, and deletion semantics.
8. Server-side plaintext access, encryption, and external inference must be consciously authorized.
9. Recovery procedures must restore notes, attachments, versions, identity, and keys consistently.

## Logical boundaries

- **Knowledge:** canonical content, stable identities, revisions, attachments, and source provenance.
- **Synchronization:** device identity, authenticated change exchange, cursors, conflict policy, and replay.
- **Ingestion:** scoped source adapters, normalization, and duplicate detection.
- **Retrieval:** keyword/semantic indexing, access filtering, and citation-grounded results.
- **Execution:** permitted model usage, tool operations, approvals, and workflow history.
- **Operations:** backups, restore verification, instrumentation, and secure deployment.

These are responsibilities, not separate projects or deployable services. A modular monolith is the default until tests and measurements justify additional components.

## Security baseline

The [threat model](security/threat-model.md) identifies data assets, attacker capabilities, trust boundaries, prioritized threats and verification ownership. [ADR 0003](decisions/0003-processing-trust-boundaries.md) requires opt-in remote processing and forbids assuming that server-readable data is end-to-end encrypted.

The concrete deployment and encryption model remains open in [#50](https://github.com/berniemackie97/Praxis/issues/50). Until its prerequisites are satisfied, no private-data remote synchronization or external model disclosure is authorized.

## Local knowledge baseline

`Praxis.Knowledge` implements immutable local Markdown note identities, revisions and a versioned ZIP interchange format. [ADR 0004](decisions/0004-portable-note-archive.md) specifies paths, integrity checks, compatibility rules and resource limits. The version-one archive contains note history; the version-two collection archive also preserves original binary attachments and stable note/attachment links. [ADR 0005](decisions/0005-attachment-collection-archive.md) defines binary integrity, deduplication and recovery limits. Neither format implements deletion state, canonical durable storage, sync, encryption, a vault watcher or remote endpoints. A separate [local attachment staging primitive](decisions/0007-local-attachment-staging.md) can resume byte transfers and publish verified content-addressed files, but is not integrated with canonical storage or authentication.

The [versioned sync contract](decisions/0006-versioned-sync-protocol.md) specifies expected revisions, tombstones, operation receipts and ordered cursors. Test-only reference and adversarial multi-device fixtures exercise these semantics, including lost acknowledgments and interrupted pulls; they do not implement persistence, authentication or remote synchronization. See the [adversarial test guide](testing/sync-adversarial.md) for evidence and limitations.

A [durable local note journal](decisions/0008-durable-offline-note-journal.md) now records pending edits, conflict state, accepted receipts, and the applied cursor across restarts. Its latest-note projection is not a full revision history or authenticated remote-sync implementation. Adapter and device integration remain open in #38 and #50.

A [local vault exchange](decisions/0009-local-vault-capture-restore.md) captures complete Markdown folders and binary originals into the collection archive and restores them to a new directory without overwriting existing data. An [incremental reconciliation contract](decisions/0010-incremental-vault-reconciliation.md) can now update a previous collection archive from a fresh scan while preserving record IDs, note histories and explicit attachment references; renamed paths require a caller-supplied map and missing baseline entries fail closed. A [read-only embedded-attachment audit](decisions/0011-vault-attachment-link-audit.md) now checks common Markdown and wiki embeds. The [version-three collection archive](decisions/0012-vault-reference-provenance.md) can opt into separately tracked, recomputed vault-derived references with explicit consent for disappearing pairs; version-one/two archives stay readable. This does not implement complete Obsidian semantics, live adapter behavior, file deletions or encrypted backup.

A [manual Obsidian desktop adapter](decisions/0013-obsidian-desktop-local-adapter.md) invokes the local CLI through explicitly triggered commands, persists archive and rename state outside the vault and refuses interrupted or unapproved updates. Its runtime is separate from the .NET knowledge library; integration tests use a simulated CLI. The [offline two-file plugin package](decisions/0014-obsidian-package-and-lifecycle.md) now builds the desktop source into a self-contained entry with simulated host tests and unload/session generation safeguards. Real Obsidian desktop event verification and mobile support remain open in #22 and #23, respectively.

The HTTP application exposes only a process liveness endpoint with its integration test. This endpoint is not a readiness signal and authenticates no user. No persistent data or sync service exists yet.

## Planned standalone ingestion and retrieval boundary

Portable archive/vault tools and the owner-local SQLite catalog with first-version original-file storage are implemented foundations. **The catalog is not yet a replay-safe, fully recoverable intake system:** source updates and interruption inventory remain #126; coordinated catalog-and-original restore remains #128. The durable ingestion pipeline, search service, first-party UI, microphone input, browser extension and MCP server are **not implemented**. The [product experience](product/second-brain-experience.md) and [delivery lanes](delivery/standalone-mvp.md) define their user-level responsibilities and acceptance criteria.

- Explicit owner-selected file/folder intake and later voice/browser/agent input all pass through one source/provenance contract (#59/#96).
- Immutable or versioned originals are separate from file paths, capture sources, extraction outputs, human annotations and searchable indexes; deduplication cannot erase source ownership.
- Processing jobs need bounded resources, durable status, cancellation/retry and verified cleanup on source deletion. Unsupported media is preserved as an original with honest processing status.
- Rebuildable lexical retrieval comes before unmeasured embedding or graph infrastructure (#60). Suggestion and relationship outputs require source-grounding and review (#97).
- The first-party local API/UI (#64) must be safe on loopback with explicit caller identity as appropriate; no network/listener/connector/remote model access is implied by running locally.
- Browser extension pairing and MCP are **different untrusted callers**, each with explicit grants. MCP read-only comes before independently approved write actions (#98).
- Folder input is a convenience surface, not the canonical data store. Obsidian is another optional adapter (#57).

The embedded local catalog design in [ADR 0017](decisions/0017-local-catalog-and-original-storage.md) has been accepted under #99. Task #122 / merged PR #123 established the owner-local SQLite catalog schema. S1b issue #124 / merged PR #125 added bounded, explicitly supplied original-stream publication into separate content-addressed files before committing first source/item/version references. This is a low-level storage operation, **not a user-facing file/folder importer**. Durable request replay and changed-source identity are #126, coherent catalog/object recovery is #128, and parent acceptance remains #100. Extraction, UI and complete ingest remain #102/#103/#108; the local caller-policy decision is #129, with cross-feature MVP acceptance #130. API authorization, UI packaging, transcription, remote trust and model/provider decisions remain separate (#107/#50/#117).

## Pending decisions

- Hosting location and device access strategy
- Trust, encryption, and on-device/server-side processing boundaries
- Attachment identity, original binary storage, and deletion/tombstone semantics
- Authentication, device enrollment, revocation, and recovery
- Sync transport, cursors, concurrency conflicts, and attachment transactions
- Mobile plugin lifecycle and background work constraints
- Measurable retrieval quality, model routing, and costs

See [delivery roadmap](roadmap.md) and the [decision records](decisions/0001-client-independence.md).
