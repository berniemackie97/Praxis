# ADR 0025: Event-confirmed file source relocation

**Status:** Proposed, implemented in draft under #105; cross-platform and hardware acceptance pending.

## Context

The canonical file-source key is a unique path. If the owner renames an already captured file, unconditional reimport under the new locator produces a separate source, losing its existing collection and review associations. Merging merely on a matching SHA-256 is equally wrong: independent copies legitimately share bytes, and a missing old path plus matching new file is not evidence of a move.

## Decision

Treat a bounded `FileSystemWatcher.Renamed` notification as **candidate continuity evidence**, not an instruction to change catalog state. A scan must independently establish that the old path is gone, the new path is inside the approved root and stable, the complete digest equals the latest recorded source version, and the source is active and the new path not already owned. A catalog transaction then updates only `sources.locator` and appends a STRICT, FK-constrained row in `source_locator_moves`. Item/revision/original/receipt/collection and derived evidence IDs are unchanged. The owner can inspect up to 20 recent moves through the existing private desktop protocol; inactive/revoked sources do not expose history through this endpoint.

The candidate event queue is bounded to 128, kept for at most one minute, and reset if the watcher is recreated/offline. A 2 GiB per-scan hash budget prevents rename processing from bypassing the normal ingestion budget. On missing/invalid/ambiguous events, ordinary independent capture proceeds. This intentionally permits a duplicate logical file after a missed event rather than risk falsely unifying unrelated documents.

Because the original request key was path and digest based, `watch-v2` idempotency keys additionally incorporate the number of historical moves **from** that path. Recreating a file at a moved-from filename cannot collide with the previous source's receipt, including after restart. Existing v1 receipts, objects and source revisions remain unchanged.

## Safety and limitations

A filesystem move event is not an inode/device identity attestation; replacement volumes, directory renames, and missed notifications still require conservative independent captures. When a revoked source is physically reintroduced under another name, it may be captured as a different source by existing policy; revoking a source identity is not a global content-ban. Earlier paths remain in the private move journal and coherent snapshots, not securely erased. No filesystem move command, remote endpoint, agent ability, model invocation, or silent category change is added.

Schema v8 is additive; v7 snapshot manifests are accepted and upgraded by the existing transactional catalog migration. Old software cannot open an upgraded catalog. The physical macOS notification, cross-platform and removable-volume trials remain release gates. A queued CI run cannot substitute for those tests.
