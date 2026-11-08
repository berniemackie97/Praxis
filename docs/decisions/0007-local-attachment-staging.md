# ADR 0007: Local staged attachment payloads

**Status:** Proposed for issue #20. This defines a local filesystem transfer primitive, not an authenticated sync endpoint or canonical collection database.

## Context

ADR 0005 retains attachment identity, original bytes and current references in bounded ZIP collection archives. ADR 0006 requires a verified payload to exist before any committed note revision may reference it. Neither implementation supports interrupted binary transfers. A local transfer must recover from ambiguous acknowledgments without ever publishing a partial or digest-mismatched blob.

## Decision

The explicit opt-in `LocalAttachmentTransferStore` uses a caller-owned directory containing `staging/` and `content/`.

- An upload is identified by a nonempty transfer UUID, a lowercase 64-character SHA-256 digest, and a declared content length (0–32 MiB). The ID is a local operation identifier, **not** authorization.
- An unfinished upload is stored in `staging/<transfer-uuid>.part`. Its first 40 bytes contain the `PTF1` marker, a four-byte little-endian length and the raw 32-byte digest. This is an internal staging header, **not** a portable public interchange format.
- `Begin` is idempotent for matching metadata and reports the offset persisted in the staging file. The caller may reconstruct the store over the same directory and continue from that offset.
- `Append` accepts sequential, nonempty chunks up to 1 MiB. The method flushes each acknowledged write. A byte-identical fully repeated chunk is accepted without duplication; conflicting bytes, partially overlapping retries, skipped offsets, and oversized data fail explicitly. When a process is interrupted mid-write, callers must read the physical offset and send only the remaining suffix.
- `Commit` requires the full declared byte length. Payload bytes are copied into a temporary file in the target content directory, flushed and hashed. Only a matching digest can be published as `content/<sha256>`. Publication uses a non-overwriting filesystem rename on the same volume. An already-published file is independently verified before reusing it, including when another upload won a publication race.
- `IsCommitted` verifies the existing blob's length and hash. It permits recovery from a lost final acknowledgment without retaining a separate per-transfer receipt. `ReadVerified` returns a verified copy; detected corruption is an explicit error rather than a silent overwrite.
- A successful publication removes the corresponding staging file. Failed validation leaves staging data available for inspection or safe cleanup by the storage owner.

Different attachment UUIDs can share one content digest. Renames preserve attachment identity without copying or changing the verified original payload. **This primitive does not manage attachment identity, references, tombstones or versioned canonical records**, and must not be used to determine whether a referenced blob can be deleted.

## Limits and failure boundaries

Individual payloads are limited to 32 MiB, matching ADR 0005; per-call chunks are capped at 1 MiB. Upload status is recovered from file metadata and length, not from a separate index. No upload is committed before digest verification. Staging files with invalid headers or lengths fail explicitly rather than being automatically reset.

This module has **no global capacity limit, retention/garbage-collection policy, backup, credential boundary, key custody, or quota enforcement for multiple clients**. The directory must be privately owned and protected by the calling application. The implementation writes **plaintext bytes to disk**; host/device encryption and private-data processing must be approved under #50 before any production integration. Never expose these methods directly as remote write or read APIs.

A completed file is made visible only after verified content has been written and flushed. Directory metadata durability, power-loss atomicity, hostile local filesystem manipulation, multi-process lifecycle coordination, and storage-provider-specific rename guarantees have **not** been established. A completed checksum is not a digital signature or authorization proof. The integration must verify filesystem assumptions, transactional record/reference commits, and recovery/backups (#18–#20, #40, #47).

Orphaned `.part` files and `.pending` temporary files after abnormal termination require explicit, authorized cleanup. Do not delete a content-addressed blob merely because one reference is removed: collection-wide reachability, tombstones, retention and device acknowledgments must be considered.

## Scope of #20

This slice implements bounded **local staging, offset resume, checksum verification and non-overwriting blob publication**. It does not complete the full issue: authenticated resumable network transfer, versioned attachment deletion, rename conflict resolution, atomic note-reference changes and user-visible recovery still require the approved trust model and subsequent implementation/integration tests.

See [ADR 0005](0005-attachment-collection-archive.md) for portable identity/archive semantics and [ADR 0006](0006-versioned-sync-protocol.md) for versioned deletion and conflict requirements.
