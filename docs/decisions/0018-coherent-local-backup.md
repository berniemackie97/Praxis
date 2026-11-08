# ADR 0018: Coherent local catalog and original-file snapshots

**Status:** Implemented library contract; physical removable-drive acceptance pending (#128).

## Context

SQLite runs in WAL mode, and originals are immutable digest-addressed files published before catalog commits. Copying live \`catalog.sqlite\` without SQLite's snapshot API can omit committed WAL state. Copying only originals or only the database cannot recover stable source/item identity, committed receipt history or historic versions. In-flight capture can publish bytes before committing metadata; those unreferenced bytes must not become required restore dependencies.

## Decision

\`LocalCatalog.BackupAsync(destinationDirectory, maxBytes)\` creates a SQLite backup through the active connection while holding the process-local catalog synchronization lock. The result is a consistent committed database view. It changes the copied database's journal mode to DELETE after backup, then enumerates **all catalog-recorded original objects** from that immutable snapshot. Each original is streamed into a new private staging directory, verified against its recorded byte length and SHA-256, and never modified at source. Objects published but not recorded in the catalog are deliberately excluded.

The manifest has format version 1, catalog schema version 2, the exact SQLite file length and digest, and a deterministic sorted digest/length inventory. A backup is promoted from a hidden \`.incomplete-...\` sibling directory to its caller-chosen final path only after an independent \`VerifyBackupAsync\` pass confirms catalog digest, SQLite integrity, schema, foreign keys, committed receipt joins, every required object and the exact on-disk inventory. There is no success marker or published backup directory before completion.

\`RestoreAsync(backupDirectory, destinationDirectory, maxBytes)\` verifies the complete backup *before* creating its own private staging directory, streams the database and originals into staging, revalidates the staged bytes, opens the recovered catalog under the normal provider and checks its original inventory. Only then is the staging directory moved into a **nonexistent** caller-selected destination. Existing or unexpectedly populated destinations are never overwritten. No automatic deletion or repair runs against source data, backups or incomplete staging.

Committed ingest receipts and revisions are copied exactly. Existing noncommitted request states, such as legacy \`queued\` records, are retained as **inert historical metadata**: restore does not schedule them, and the current direct capture API refuses replay of an unfinished receipt. #102 must define future job recovery separately. Derived caches, uncommitted staging, unknown published object orphans and external source files are not part of this local snapshot.

## Limits and invariants

- The caller supplies an absolute new destination under an existing owner-private parent. Unix directory/file modes are restricted; Windows uses inherited ACLs and **requires owner verification**. A backup destination inside its source, or a restore destination inside its backup, is refused. Same-user malicious processes and concurrent directory replacement are outside this contract.
- The caller supplies a positive total-byte budget (SQLite plus recorded originals); the inventory is limited to 100,000 objects and the JSON manifest to 32 MiB. File transfers and digest verification stream with bounded buffers, but database snapshot creation is synchronous and a large SQLite catalog may block cancellation until the database copy returns. Physical multi-GiB behavior has not been established.
- Existing data-root locking prevents two Praxis catalog instances from opening in the same process/user scope; code using SQLite directly without the lock is not governed by the capture contract. SQLite's backup API still provides a transactional database view. Sources published after that view are not included.
- Backup validation verifies accidental damage and mismatches, **not cryptographic authenticity**. Someone able to replace both manifest and catalog, or tamper with unencrypted plaintext files, is not defeated by SHA-256. No encryption, signing, cloud upload, key custody, schedule, incremental retention or OS-level snapshot is provided (#40/#50/#65).
- Staging directory names explicitly signal incompleteness. Cancellation, missing media, permission failures, full storage, interrupted power or process termination can leave private staging behind; the last completed backup and existing library are not changed by cleanup. Such directories must **not** be treated as backups. Inspect and remove them manually only after validating completed copies.
- The final directory rename is on the same parent filesystem. This improves publication ordering but does not prove durable metadata after a power loss or sudden removable-media unplug. The caller must not treat success as proof of fsync behavior of every drive/OS combination; restore verification is required before trusting a recovered copy.
- No user-facing backup UI or owner CLI is shipped by this library slice; #108/#130 own end-to-end use and visible failure states.

## Verification and acceptance

Automated tests exercise active WAL snapshot/revision history, stable identities and historical committed receipts, post-backup source mutation, independent sources with shared content, unpublished orphan exclusion, missing/corrupt originals, truncated catalog, incompatible manifest, unexpected entries, quota refusal, existing target refusal, pre-cancellation and inert unfinished receipt history. Cross-platform CI must pass before review.

The following acceptance remains **not verified** by CI and must be recorded against #128 and #130 before claims of real-device recovery:

1. Export a genuinely large (>2 GiB) representative source and restore on an owner-approved filesystem, verifying exact bytes and receipt history.
2. Interrupt export and restore through simulated disk exhaustion *and* actual permission/unmount faults; verify old live data and last completed backup remain intact, and incomplete siblings are visibly rejected.
3. On macOS, validate disconnect/reconnect of the real external volume, incomplete directory handling, SQLite WAL behavior and fresh-directory restore using exact checksums.
4. Inspect security permissions and storage capacity, backup rotation/retention and any expected drive-specific data-flush guarantees. Later encrypted offsite recovery belongs to #40.

## Relationship to other work

S1a #122 established SQLite v1, S1b #124 original publication, and merged S1c #126 idempotent revisions/receipts and read-only recovery inventory. This decision addresses the automated coherent-snapshot component of S1d #128, **not** background ingestion #102, the first-party experience #108, or full operational recovery #40. The parent #100 remains open until all S1d acceptance evidence and integrated storage checks are met.
