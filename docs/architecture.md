# Architecture baseline

Praxis is a client-independent knowledge and automation platform. Obsidian is the first supported editor; first-party clients will use the same versioned contracts.

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

The HTTP application exposes only a process liveness endpoint with its integration test. This endpoint is not a readiness signal and authenticates no user. No persistent data or sync service exists yet.

## Pending decisions

- Hosting location and device access strategy
- Trust, encryption, and on-device/server-side processing boundaries
- Attachment identity, original binary storage, and deletion/tombstone semantics
- Authentication, device enrollment, revocation, and recovery
- Sync transport, cursors, concurrency conflicts, and attachment transactions
- Mobile plugin lifecycle and background work constraints
- Measurable retrieval quality, model routing, and costs

See [delivery roadmap](roadmap.md) and the [decision records](decisions/0001-client-independence.md).
