# ADR 0006: Versioned synchronization and conflict semantics

**Status:** Accepted in PR #76 for the local protocol contract. This is not an implemented network or durable synchronization service.

## Context

Praxis already preserves Markdown histories (ADR 0004) and original binary attachments (ADR 0005). Neither archive represents concurrent writers, tombstones, offline replay, device authorization or a durable change cursor. Client timestamps, filenames and file modification times cannot establish causal order across devices.

The host and encryption/trust choices in #50 remain unresolved. This contract must therefore work for an authorized processing service or an opaque encrypted-payload service without pretending those modes offer identical server-side validation.

## Decision

Use **optimistic expected-revision writes, an append-only ordered change log, device-scoped idempotency and explicit conflicts**. Do not use last-write-wins, time-based conflict resolution or an automatic text CRDT in the first protocol.

This is not a database, crypto, hosting, HTTP endpoint or identity-provider decision.

### Identities and wire envelope

| Field | Contract |
| --- | --- |
| protocolVersion | Required schema number; initial protocol supports version 1 only |
| collectionId | Stable UUID for the collection; never an authorization capability |
| collectionEpoch | Opaque UUID for one compatible log generation; changed after incompatible rebootstrap |
| deviceId | Bound to authenticated device enrollment; the request field alone proves nothing |
| operationId | UUID generated once for an intended mutation; retained unchanged for retries |
| recordId | Stable note or attachment UUID; never derived from a filename |
| expectedRevision | Current per-record integer revision the client observed; zero for a new record |
| action | Upsert, Delete, or an explicitly requested Restore |
| payload | Full resulting record projection (or verified blob reference and link set), not an implicit merge |
| clientTime | Advisory only; it cannot order writes or break conflicts |

Mutation identity is (collectionId, collectionEpoch, authenticated deviceId, operationId). A successful operation returns a durable receipt with its resulting record revision and global collection sequence. An exact replay returns that same receipt, even if later writes have changed the record. Reusing an operation key with different content is rejected.

The executable fixture models one collection and uses the epoch as its scope. It does not simulate authentication or a public API.

### Mutation state transitions

- Upsert with expectedRevision = 0 creates revision 1 only if the record UUID has never existed. An existing tombstone still occupies its UUID.
- Upsert against a live record requires exactly its current revision. Content edits and renames are each versioned updates; a rename is never a new identity.
- Delete requires the current live revision and creates a **tombstone** with a new revision. It retains the record UUID and the preceding content/revision reference for recoverability; deletion is not immediate physical erasure.
- Upsert cannot resurrect a tombstone. Restore requires the current tombstone revision and creates another revision. A stale restore cannot overwrite intervening changes.
- Stale write, delete/edit race, rename/edit race, or active path collision returns a conflict without changing state or advancing the sequence. Authorized clients can inspect the current revision and retain their pending local contribution until explicit resolution.
- Case-insensitive path uniqueness applies to a filesystem-facing collection projection. It is enforced where the selected trust model allows the relevant path metadata to be inspected. A ciphertext-only service cannot claim to enforce hidden plaintext paths.
- Deleted paths may be reused by another record. A subsequent restore must choose a nonconflicting path rather than stealing it.
- Deleting an attachment with live references requires an explicit versioned reference update or must be rejected; it cannot leave silently broken links.

An accepted mutation commits its record revision, collection event and idempotency receipt **atomically**. A failure before commit cannot acknowledge success; after an ambiguous response, an identical retry returns the committed receipt. Rejected operations cannot create change events. Actual transactional durability must be demonstrated in the persistence slice; the fixture is memory-only.

### Ordered pull and recovery

Every accepted change gets a strictly increasing collection sequence. A pull request provides collectionEpoch and an **exclusive** sequence cursor. The server returns events after that cursor, a next cursor equal to the last event returned (unchanged for an empty page), and the current high-water sequence. Events provide record identity, revision, action/tombstone state and enough authorized state to project the result. Binary content travels separately by validated hash.

A client advances its *durably applied* cursor only after atomically committing the page locally. Merely receiving a response is not acknowledgment. The same page may be delivered again; sequence projection must be idempotent. Pending outbound mutations must be durably stored separately from the pull cursor.

A future, invalid or foreign-epoch cursor fails explicitly. A cursor older than the retained log floor returns a **reset/snapshot required** response; no silent gap filling or partial replay. Snapshot and head cursor must represent one consistent point-in-time view, including tombstones and attachment links. Pending local mutations are preserved through snapshot refresh and must be rebased or presented as conflicts.

Compaction is conditional on an explicit retention policy: do not prune records/events/receipts that can still be needed by authorized devices unless those devices are required to rebootstrap safely. Backups, indexes and caches have separate deletion/erasure requirements. An acknowledgment does not imply secure erasure of historical versions.

Pagination, payload sizes, bandwidth, CPU, canceled writes and staged-file lifetimes require independently enforced limits under #19. The fixture bounds pages to 1–100 events for reference testing only; it does not establish production throughput or memory budgets.

### Attachment and reference consistency

Attachment records have independent revisions, stable UUIDs and content digests. Binary content may be staged resumably but must pass integrity checks and be durably present before a committed note revision references it. A note's current attachment-link set changes atomically with its note revision and must resolve to authorized attachment IDs. A client cannot infer attachment identity from a filename or hash alone.

ADR 0005 archives the **current** link set but does not version historical links for every note revision. That archival limitation must be resolved before claiming full revision-by-revision reference recovery. Remote blob transfer, integrity verification, attachment deletion and reference transactions are tracked in #20; the durable sync model is #18 and #19.

### Schema, security and observability

- Unknown or unsupported protocol versions fail before any state mutation. No silent downgrade that strips tombstones, permissions or unknown fields.
- A compatible migration preserves record IDs, record revisions, event ordering and accepted-operation receipts. An incompatible log restore starts a new epoch and requires controlled rebootstrap and pending-edit preservation.
- Device identity, collection ownership, per-record permissions, authorization-aware conflict bodies, transport security, quotas and privacy must be enforced before accepting mutations or reading logs (#13, #15, #19, #50).
- A UUID, sequence or operation key is never an authorization token (RFC 9562).
- Operational diagnostics use safe reason codes, correlation and sequence metadata. Do not log note text, attachment bytes, secrets or unrestricted conflict payloads.

The selected plaintext-versus-ciphertext processing model determines which validation can happen server-side. This ADR cannot approve remote writes while #50 and authentication/recovery gates remain open.

### Alternatives rejected

- Last-write-wins or timestamps: clock skew/reordering silently destroy an offline branch.
- One global sequence as the only write precondition: independent records conflict unnecessarily.
- Starting with CRDT/diff merge: requires explicit text, metadata and binary conflict policies not yet justified by evidence.
- Filename identity or hard removal on delete: loses rename continuity and permits silent offline resurrection.
- Treating v1/v2 ZIP interchange snapshots as the change log: archives do not contain tombstones, device receipts, authorization or incremental cursors.

## Executable acceptance

The test-only fixture in tests/Praxis.Knowledge.Tests models accepted revisions, operation receipts, tombstones, conflicting edits, rename collisions, clock skew, pagination, cursor expiry and epoch/version rejection. It is **not** a synchronization engine, an authentication system, a persistence guarantee or an end-to-end recovery test. [ADR 0008](0008-durable-offline-note-journal.md) separately defines the explicit local checkpoint, offline outbox and restart behavior. Deterministic multi-device failure schedules are documented in [the adversarial test guide](../testing/sync-adversarial.md) (#21); this still does not validate real persistent or network transport.

References: RFC 9110 section 13.1.1 (strong conditional writes and lost-update prevention), RFC 9562 (UUID format and security considerations).
