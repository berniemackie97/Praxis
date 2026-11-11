# ADR 0040: Bounded owner-private watched-intake incident journal

**Status:** Proposed in #105 / PR #263. Requires migration, snapshot and owner-machine acceptance before release.

## Context

Per-root watcher state and file errors describe the current worker session, while already captured originals and processing jobs survive restart. After an interrupted scan, disconnected volume or watcher failure, the owner needs a reliable record of when intake stopped and resumed. A volatile status snapshot or unbounded stderr stream cannot provide that record.

## Decision

Store significant transitions of owner-approved watched roots in the private SQLite catalog under additive schema v17. Each incident has a catalog-generated integer ID, canonical root path, one fixed state (`offline`, `degraded`, `reapproval_required`), start UTC and optional resolution UTC. The next different fault closes the previous open incident and begins a new one atomically; a verified return to `watching` resolves an open fault. A repeated state never produces a duplicate. Scan progress, pause, source bytes, exception details and transient file-level error messages are not persisted in this journal.

The database allows at most one unresolved incident per root and retains at most 512 recent entries globally, returning no more than 100 on a bounded read. The worker's existing watcher status transitions supply evidence; the Application layer sees only `IIntakeIncidentJournal`. SQL remains within Infrastructure.Persistence, the owner's typed desktop protocol returns the 20 most recent incidents, and SwiftUI presents the eight newest. The catalog's serialized writer and read-only query connection handle concurrency; every modification is transactional and uses the injected `TimeProvider`.

## Consequences and limits

The catalog migration and coherent snapshot automatically include the new journal, including open incidents. Opening the catalog with v17 upgrades its schema; older binaries cannot reopen it. Backup/restore and downgrade compatibility must be verified before a release. The journal is intentionally a bounded operational history, not an append-only forensic audit log; older entries, including unresolved entries when the cap is reached, may be pruned. Root paths themselves can be private, so they remain only in the protected local catalog/stdio UI and should not be copied into public CI output.

No incident automatically revokes a source, deletes its original, or grants permission to scan a reattached path. Matching device/inode metadata is a local safety signal, not cryptographic volume authentication. Physical unplug/replug, actual overflow and disk-full acceptance remain separate #105 gates.

## Rejected alternatives

A process-side JSON history would create a second persistence owner, omit atomic catalog recovery, and risk unbounded or unprotected source paths. Using only the last in-memory status loses failures across restarts; copying stderr messages into the catalog would store uncontrolled exception details and sensitive paths. Per-file logging of every notification would generate high-volume records without corresponding durable facts.
