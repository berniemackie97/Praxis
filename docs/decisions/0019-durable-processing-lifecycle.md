# ADR 0019: Durable local processing jobs and source revocation

**Status:** Implementation under review (#102); source ingestion adapters and worker execution not yet shipped.

## Context

Praxis already commits immutable originals, source/item revision history and idempotent capture receipts. There is no persisted derivative processing state: a restart cannot tell which item revisions still require text extraction, and a source revocation has no API to invalidate unfinished work. The existing `ingest_requests` table stores *original capture receipts*, so changing a committed receipt to `queued` would destroy its idempotent replay guarantee.

## Decision

SQLite schema v3 adds `processing_jobs` keyed by (item ID, revision, processor key), linked to immutable `item_versions`. Each newly committed revision schedules `source-text-v1` atomically with its version and capture receipt. Repeated committed request keys and new request keys carrying identical content do not enqueue extra work. The upgrade backfills one job for each existing v2 version; revoked sources receive canceled jobs. Historical receipts remain unchanged.

Jobs have explicit `queued`, `running`, `completed`, `unsupported`, `failed` and `canceled` states. A bounded claim leases the oldest eligible job for 1 second to 1 hour and increments an attempt count transactionally. Another claim after lease expiry generates a new lease token. A worker must provide that token to finish work *before expiry*. Stale, canceled or revoked claims cannot finish. Exhausting the bounded attempt budget marks the job failed with the metadata-only `attempt_limit` code; an explicit retry resets the current attempt window while retaining lifetime attempt count. A worker crash never silently marks a job complete.

`RevokeSource` changes an active source to revoked and cancels outstanding queued/running/failed processing in the same transaction. Already published original bytes and historical revisions remain immutable; committed capture replay for the revoked source is rejected by the existing contract. Derived search, caches and any future external access MUST independently filter by the active source grant, including for completed jobs. Revocation alone is not secure erasure.

The public library exposes a bounded list of jobs, explicit enqueue for a processor revision, claim, finish, retry, cancel and revoke operations. None of these methods is a network endpoint. A direct owner-local intake helper accepts explicitly supplied files or a scoped folder, rejects symbolic-link selections and discovered descendants, enforces bounded depth/count/byte budgets and uses deterministic per-batch file request keys. It is not a file chooser, authorization service, watcher, worker, PDF parser, derivative text store, source-deletion service or UI. The future processor must commit its output and its terminal status under a single transaction when derivatives are implemented (#103); `completed` in this base contract only describes a trusted worker's explicit acknowledgment, not proof of extracted text.

## Compatibility and recovery

- Migration is additive v1 → v2 → v3 (or v2 → v3) and retains source/item/original/revision/receipt identity. Existing v2 library backups remain independently verifiable and restorable; restore then upgrades the recovered catalog to v3 in staging. New backups use schema v3, and include job rows with their captured states.
- Restored queued jobs are **not automatically dispatched**. Restored running jobs retain their lease deadline and can be reclaimed only after expiry. This is an explicit owner-local library contract, not yet a scheduler or end-user progress UI.
- A processing job may be canceled after a worker started parsing; the worker must obey the fencing token. The library cannot forcibly terminate arbitrary in-process work. Future worker execution must pass cancellation to parsers and check the lease before publishing derived content.
- A failed job stores a bounded failure **code only**. Do not persist exception messages, full paths, original text or raw stack traces in job metadata.
- The source path accepted by `CaptureFileAsync` is still caller-provided provenance. A first-party chooser must establish permission before calling the library. Direct folder selection is bounded and rejects symbolic links within the selected tree; it does not authenticate the chooser, reject arbitrary symlinked ancestors outside the chosen boundary, or eliminate path replacement races. A trusted caller must still choose the folder (#101). Loopback, browser and MCP access remain disabled until their separate trust and caller authorization decisions (#129/#107/#117).
- Duration and attempt bounds do not impose a global per-library queue capacity or CPU/memory budget. #102 still owns worker concurrency, quota, interruption injection and differentiated erase/unlink behavior. Closing this PR does not close #102 or the first-party MVP gate (#130).

## Verification and follow-ups

Regression tests check new revision and same-byte scheduling, v1/v2 migration backfill, old v2 snapshot restore, restart/expired-lease recovery, stale completion rejection, retry exhaustion/manual reset, cancellation, revocation and preservation of originals and job state through coherent backup/restore. Cross-platform CI must pass before merge.

Next implementation: first-party file/folder selection and visible Inbox (#101), bounded worker/extraction (#102/#103), derived invalidation and lexical search (#106), with caller authorization (#129/#107) before exposing private-data HTTP endpoints. Full external-media backup durability remains a separate physical test gate (#128).
