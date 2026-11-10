# ADR 0023: Owner-authored collections in the local source catalog

**Status:** Proposed in #146; implementation and macOS acceptance pending.

## Context

The running watcher captures originals by durable source identity, not by a permanent physical folder hierarchy. A user needs to organize many files under projects/topics without moving their originals or waiting for an inference provider. Merely displaying FTS results does not establish a useful Second Brain.

## Decision

SQLite schema v6 adds two STRICT tables: `source_collections` holds an owner-created UUID, display name, normalized unique key, creation time and optional archive time; `source_collection_memberships` records distinct owner-controlled collection/source UUID pairs with a creation timestamp and foreign-key fences. A source belongs to many collections and vice versa. Each assignment persists through a source revision because it references `source_id`, not item revision, path or SHA-256.

List queries expose only active sources under non-archived collections. Revocation does not wipe membership history or content-addressed originals; source-active SQL predicates prevent a revoked source from being surfaced through counts, source membership lookup and collection-filtered library results. Archiving a collection hides it from ordinary navigation without deleting its user-authored history. Names are validated and uniqueness keyed by Unicode-normalized, invariant-uppercase trimmed text; no generated names are silently applied as owner choices.

The trusted first-party process invokes bounded, parameterized catalog methods through the existing allowlisted inherited-pipe bridge. No extra store, private HTTP route, cloud sync or model component is introduced. Collection creation, rename, archive, add and remove all require explicit user actions; ordinary ingestion remains deterministic and autonomous only for verified originals, extraction and indexing.

The migration runs transactionally from v1–v5 to v6. Schema validation and coherent backup/restore operate on the upgraded catalog; old v5 backup manifests remain verifiable and upgrade during restore. Downgrade is rejected rather than silently truncating new tables. The current source original-object retention policy is unchanged.

## Privacy and authorization boundary

`source_collections` and `source_collection_memberships` are **organizational views only**, not encryption, tenant, account, synchronization or authorization boundaries. Their many-to-many membership cannot define key custody or the single authoritative owner of an original source. [ADR 0003](0003-processing-trust-boundaries.md) requires a separately enforced **security space** per source/revision for future sharing and encrypted/hosted deployments; a user may organize authorized sources from multiple spaces into one view, but every list, count, search, excerpt, derived fact and action must separately check the caller's actual space/source grant. Adding a denied source to an organizational collection never confers access. This distinction is a future implementation gate (#13/#15/#28), not an assertion that current local SQLite tables already implement multi-user ACLs.

## Rejected alternatives

- Organizing by disk folder move: would violate watched-source preservation and make rename/path attribution the organization authority.
- Separate JSON or UI-only collection store: would create conflicting durability, recovery and source-ID lifecycles.
- Automatically accepting model classifications: would confuse tentative derived interpretation with an intentional user-authored decision.

## Verification and unresolved risks

Tests must show name constraints and duplicate protection, idempotent membership operations, source revision continuity, revocation filtering, archive behavior, v5 migration and coherent backup/restore. Real macOS create/edit/reopen/keyboard and multi-source UI workflows require owner evidence; Linux/Windows CI remains blocked by #137. A merged change with unexecuted checks is not a completed release gate.

Historical collection membership remains in private SQLite pages/backup snapshots after revoke/archive; no secure-delete guarantee is implied. A large collection tree and multi-user permission model are not delivered. #115 owns processor-backed proposal provenance and human review; #116 owns richer inferred/explicit relationships and dynamic saved queries; #117–#119 own independently authorized external agent access. Original sources remain canonical. 
