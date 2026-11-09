# ADR 0029: Processing pipeline independent of source intake

**Status:** Accepted. Schema v9.

## Context

Watched intake ran processing inside its scan: after importing, `LocalWatchedIntake` executed up to 64 text jobs and then 16 heading proposals under the same scan lock. Any slow processor therefore delayed capture, and the lifetime of every processor was bound to the filesystem watcher. Heading proposals used a second scheduling mechanism (polling for revisions without an `enrichment_runs` row) with no lease, attempt budget or failure state. Model-backed processors planned for #115/#31 need fencing, retries, bounded concurrency and cancellation, and must never stall intake.

## Decision

- **One scheduler.** All processors run as leased `processing_jobs` rows. `LocalProcessingPipeline` claims work for registered `ILocalProcessingHandler`s, in rotation, with a per-handler `MaxConcurrency` (1–16) and `LeaseDuration`. It runs as its own service beside intake; the worker composes both so the first to stop or fault cancels the other and every fault is surfaced.
- **Wake, then poll.** Intake calls `onCaptured` after it imports, and the desktop protocol wakes the pipeline after a note capture or retry. An idle poll (15 s by default) recovers expired leases and work queued by other paths.
- **Stage graph.** `LocalProcessors.DependentsOf` declares which processors consume another's output. Completing a stage enqueues its dependents in the same transaction that records the completion (`source-text-v1` → `markdown-heading-v1`). Dependents are revision-keyed and idempotent (`ON CONFLICT DO NOTHING`).
- **Heading proposals on leases.** The producer reads verified text only while it holds a lease on an active source and commits its run, proposals and job completion in one transaction. Non-Markdown sources complete as `unsupported`. The polling query was removed.
- **Fault isolation.** A handler exception that is not a shutdown fails only that job with the code `processor_fault`; the report names the processor, item, revision and exception type, never its message. Shutdown leaves the lease `running` so it expires and is reclaimed.
- **Reopening work.** `TryReopenWork` lets a handler requeue work its current version can now process (text uses it for sources earlier marked `unsupported_format`). The pipeline calls it when idle until it reports nothing more.

## Migration (v8 → v9)

Adds `processing_jobs_processor_ready (processor_key, state, created_utc)` for per-processor claims. Backfills heading jobs: `completed` where a heading run exists; otherwise, for every completed text extraction, `queued` for the current revision of an active source and `canceled` for superseded revisions or revoked sources. Unsupported or failed text gets no heading job. The migration is additive and transactional; v2–v8 snapshots still verify and restore through the normal upgrade path.

## Consequences

- Capture is never blocked by processing; search and proposals become eventually consistent with capture. Tests and UI must wait for processing state, not only for a new revision.
- Every stage reports durable per-revision state, so the library and topic outcome read job state instead of inferring from file extensions.
- All catalog access is still serialized on one connection (ADR 0028), so concurrency helps I/O-bound handlers (original reads, future model calls) rather than SQL throughput. Long-running handlers must keep work outside catalog callbacks and commit briefly under their lease.

## Not yet addressed

Lease renewal for runs longer than their lease, skipping superseded revisions before expensive processing, per-processor status in the desktop protocol, and segmenting text for model context. These belong with the first model-backed processor.
