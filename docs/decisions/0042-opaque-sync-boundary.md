# ADR 0042: Authorized transactional opaque synchronization storage

**Status:** Proposed implementation in #19. The first implementation resides in the Infrastructure synchronization and identity registry layers. No private network endpoint is deployed or certified by this change.

## Context

[ADR 0006](0006-versioned-sync-protocol.md) defines expected-revision writes, durable receipts, ordered change cursors and conflicts. [ADR 0003](0003-processing-trust-boundaries.md) requires client-held encrypted spaces to remain opaque to their host. [ADR 0038](0038-private-device-certificate-identity.md) provides library-level device certificate and signed-operation verification. [ADR 0041](0041-hybrid-local-first-deployment.md) keeps the D0 owner-local first-party pipe and health-only API separate from D1/D2 private hosting.

The existing INoteSyncExchange contains **plaintext local note projections**, not an approved remote wire DTO. A remote service requires an independent security-space-scoped protocol and durable data boundary.

## Initial implementation

- OpaqueSyncGateway requires a previously authenticated TLS client certificate identity and one-use signed device proof supplied by the identity subsystem. The request binds device, space, permission and current content-key epoch. Revoked grants/devices, disabled principals, wrong certificate/proof binding, wrong class, absent key and outstanding rekey fail before storage.
- AuthenticatedDataAccess.ExecuteSpace holds the registry revocation gate through a **synchronous** protected data operation. A source-specific proof cannot become an unrestricted whole-space proof. Store read/write methods are internal and cannot be invoked by arbitrary network handlers with fabricated identities.
- Owner-private SQLite WAL/FULL storage enforces exclusive process ownership, an explicit schema version, integrity/foreign-key checks and operator-provisioned collection epoch. A remote request cannot create or reset an epoch.
- Each accepted mutation atomically commits record revision/tombstone, strictly ordered event, and device/operation-scoped request hash and immutable receipt. Identical replay returns the exact original receipt after restart; changed replay is refused. Optimistic conflicts do not advance sequence or disclose previous ciphertext to a writer lacking read access. UUIDs cannot move between security spaces.
- Pull uses exclusive cursors, transaction-consistent head, 1–100-event pages and explicit protocol/epoch/cursor errors. Opaque payloads are limited to 64 KiB. An initial 10,000-event-per-space and 128 MiB stored-byte budget refuses writes before unbounded growth; these ceilings are **not measured release capacities**.
- Event and record key epochs are retained. Following a key rotation, operations that would expose old-key ciphertext return ResetRequired. Full authenticated re-encryption and rebootstrap **do not yet exist**, so safety takes precedence over availability. Initial schema has no pruning or snapshot promotion; CursorExpired remains a future retained-log behavior.
- The host stores only opaque bytes supplied by an authorized device. It cannot verify that those bytes were produced by AEAD or inspect encrypted path/link constraints. A future client adapter must enforce versioned authenticated encryption, bound associated data, trusted recipients, anti-rollback and decryption before accepting a record. **The present tests use synthetic opaque bytes; this is not tested end-to-end encryption.**

## Operational and security boundaries

- On Unix, a private root and owner-only database/lease permissions are required. Windows effective ACLs, ancestor symlinks and physical media failures need separate real-device tests.
- The identity registry and sync store use **two distinct databases**. Sync record/event/receipt updates are atomic within the sync store while the identity registry gate prevents in-process revocation interleaving. This does not make both databases globally atomic for provisioning or backup. #65/#40/#46 own coordinated recovery of identity, sync data, originals and independent key factors.
- No actual HTTPS/mTLS handshake, server certificate custody, credential renewal, network request cancellation, attachment upload, rate throttling, verified ciphertext-only client, or two-device production convergence is implemented yet. Do not expose the gateway directly on HTTP or rely on VPN membership, forwarded certificates, caller UUIDs or plaintext client DTOs.
- The repository-side tests require execution on the exact PR head; GitHub Actions #137 remains queued. The tests distinguish in-process signed proof/permission enforcement from actual TLS and protocol coverage.

## Remaining #19 acceptance

1. Add a separately enabled actual TLS client-certificate transport on an operator-approved D1 host, deriving peer certificates only from its real TLS handshake, with pinned CA/revocation and signed one-time proof per operation. Keep D0 first-party pipes and health-only API unchanged.
2. Implement the actual client-side encrypted wire adapter, key/space/source binding, safe new-source enrollment, full key rotation and independently verifiable snapshot rebootstrap. The host must never process plaintext for client-held security spaces.
3. Deliver attachment staging/integrity/atomic reference changes (#20), retention/compaction, rate/storage/concurrency limits, cancellation/disconnect semantics and real two-client persisted integration (#18) with negative tests.
4. Execute exact-SHA restore/build/format/test, live TLS and physical recovery/load tests; reconcile status and dependencies with #56/#65/Project #3 only on verified completion.

**#19 stays open until these remaining behaviors are actually implemented and demonstrated.**
