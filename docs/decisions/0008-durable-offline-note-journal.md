# ADR 0008: Durable offline note journal

**Status:** Proposed for issue #38. This decision covers an explicit, local-only client checkpoint. It does not authorize a remote synchronization service or replace portable note-history archives.

## Problem

The synchronization contract (ADR 0006) and adversarial fixtures exercise retries and conflicts in memory. A real offline client also needs to preserve pending edits, acknowledged writes awaiting pull, remote revision/cursor state, and unresolved conflicts when the process terminates or an upload acknowledgment is lost.

## Decision

`LocalNoteJournal.Open(directory, collectionEpoch, deviceId)` creates or opens an explicit client-owned local journal. The directory belongs to exactly one collection epoch and local device identity. Neither UUID grants access to anything. The journal takes an exclusive filesystem lease for its entire lifetime; a second process cannot mutate the same directory concurrently. Callers own directory permissions, encryption, backup and lifecycle.

The journal persists one versioned JSON checkpoint containing:

- Latest observed remote note projections, with stable UUID, revision, path, UTF-8 Markdown and deletion flag. A tombstone retains the previous text and path to permit recovery.
- An ordered offline outbox with a stable per-attempt operation UUID, expected revision, explicit upsert/delete/restore action and full authored content.
- Conflicting changes with the rejected original and reported current state, plus preserved originals for conflicts explicitly resolved by the user.
- The durable applied server cursor and accepted operation receipts awaiting their corresponding pull events. This avoids applying an acknowledged local revision twice when it later appears in a change-log page.

Every mutation builds a separate candidate state, validates it and writes the entire checkpoint to a uniquely named staging file. That file is flushed and then renamed over the previous checkpoint on the same filesystem. The live in-memory state is replaced **only after** the checkpoint publication succeeds. Temporary files left by a crashed process are never replayed as authoritative state. SHA-256 covers the checkpoint payload for corruption detection, not authentication. Malformed, unsupported, inconsistent or mismatched-identity files fail explicitly rather than resetting queued edits.

## Editing and synchronization semantics

- A client can queue several edits to one note without network access. Each edit gets an independent operation ID and sequential expected revision. Only the first unacknowledged operation for a note is offered for transmission.
- Accepted-operation receipts include the server's accepted sequence and exact resulting note revision/content. After durable acknowledgment, the next queued edit may become eligible. On subsequent ordered pull, the echoed event is compared against its saved receipt; a mismatch fails before advancing the cursor.
- Pull pages contain at most 100 ordered events. The expected cursor and every resulting revision are checked before one transactional checkpoint update. Invalid, reordered or repeated pages are rejected rather than partially applied.
- A remote change that invalidates a queued operation creates an explicit conflict and blocks further sends for that note. The local contribution remains in the outbox. A resolution creates a **new operation** against the observed current revision and preserves the rejected original in resolved conflict history. Multiple dependent queued edits must be considered explicitly; the implementation refuses to drop or automatically rewrite them.
- Snapshot rebootstrap is allowed only for the same epoch. It replaces the remote projection and applied cursor while retaining pending local edits and conflicts. A different epoch is rejected: migrating unresolved edits to a new epoch requires a separate approved workflow.
- Paths are portable Markdown paths; active names are case-insensitively unique. Pending creates and renames are checked against other queued paths.

## Bounds and limitations

The checkpoint contains up to 10,000 current note projections, 2,000 pending operations, 2,000 unresolved and 2,000 resolved conflicts, a 24 MiB serialized payload and a 33 MiB on-disk envelope. Individual note text is limited to 1 MiB of UTF-8. Operations fail without changing live state if these limits are exceeded. Each update rewrites the bounded snapshot rather than appending to an unbounded journal.

**This is local plaintext storage.** Keep the directory private; the journal provides no key management, filesystem sandbox, hostile symlink defense, API authentication, rate control or remote data transfer. The per-process lease and replacement strategy are not a claim of tested power-loss durability on every filesystem. Directory-entry syncing, backup snapshots, schema migrations, secure deletion, real device lifecycle and disk-full recovery must be separately verified before production use.

The journal stores **latest note projections**, not full immutable `NoteRecord` revision histories or original attachments. The portable archive and local attachment transfer store remain distinct components; an adapter must bind them transactionally before claiming complete vault persistence. This type is not a canonical server database and must not be used as one.

## Evidence and follow-up

Unit and integration tests cover restart, multiple offline edits, concurrent in-process writers, exclusive lease, malformed data, receipt replay, interrupted pages, conflict preservation, explicit merge and two client journals converging through the ADR 0006 reference server. They demonstrate local checkpoint behavior under test conditions, not actual network, multi-process power loss or authenticated device synchronization.

Issue #38 remains open until adapters use these contracts and complete migration, lifecycle and partial-failure tests. Further gates are #15 (identity), #18–#20 (real sync/attachment transactions), #25 (vault recovery), #40 (backups) and #50 (hosting, encryption and consent).
