# ADR 0022: Rebuildable owner-local full-text search

**Status:** Implemented in proposed #106 slice; verification and review pending.

## Context

Praxis stores immutable originals, revision processing jobs and verified text derivatives in the owner-local SQLite catalog. The initial user-facing experience needs local search with source attribution and no model provider, external API or Obsidian dependency. Source updates and revocations cannot leave searchable hits from superseded or inaccessible revisions.

## Decision

SQLite schema v5 adds a derived FTS5 index (`text_search`) over the source locator and verified text. A small `STRICT` row-ID mapping (`search_index_keys`) holds the item ID, revision and source ID, indexed by source and uniquely keyed by item/revision. The index is not canonical source data. It can be reconstructed solely from `text_derivatives`, `processing_jobs`, `item_versions`, `items` and `sources` via the `eligible_text_search` view.

Only the **latest version** of an active source with a completed, digest-matched `source-text-v1` derivative is eligible. Schema-v5 migration backfills eligible v4 derivatives. The migration is transactional; v2/v3/v4 coherent snapshots remain separately verifiable and restore through the existing catalog upgrade path. New snapshots contain the derived index, but its rows are not the recovery authority.

In the same SQLite transaction as a new source revision, an item-version trigger deletes the previous FTS row by indexed mapping. When a source is revoked, a source-status trigger removes its indexed entries without changing the original object or another source's independent attribution. When a text job commits successfully, its processing-state transition inserts the new eligible revision's mapping and FTS row in that transaction. There is no asynchronous window in which an obsolete index hit should become current. Query-time SQL also requires active source status, a matching original digest, the latest revision, a completed derivative and a completed job, so a stale or manually reintroduced FTS row cannot bypass canonical visibility checks.

The owner-local `LocalCatalog.Search.Query` library API performs FTS5-ranked literal-term AND matching. Query text is tokenized to letter/digit terms rather than accepting arbitrary FTS expression operators; all SQL parameters are bound. It accepts at most 256 query characters, 16 terms of up to 64 characters and 100 results. Results return source and item IDs, revision, exact original SHA-256 digest, source path/name and a capped 480-character excerpt with visible highlighting brackets. It uses FTS5 BM25 ordering with stable source/item tie-breaks. Returned snippets are **untrusted user content**, not safe HTML; UI renderers must escape them.

`Search.Rebuild` deletes and rebuilds index entries and mapping rows transactionally from the canonical eligibility view. It enforces at most 100,000 records and 512 MiB of source text (tighter caller budgets allowed) before mutation. SQLite command timeouts are set, but the connection's busy timeout is **not** a proven hard CPU-time limit. Large corpus performance, cancellation, process interruption and drive exhaustion require additional benchmark/negative-test evidence under #29/#47. A rejected rebuild leaves the old index intact.

## Trust and deletion boundaries

The index, paths, original digests and excerpts are plaintext private owner-local content. No private search route, browser access, remote sync, model inference, agent tool or external disclosure is introduced. ADR 0021 and #107 remain the future caller-authorization boundary, and #117 separately gates MCP read tools.

Removal/revocation removes query visibility and live FTS rows, but SQLite database pages, WAL, historical backups and copied original bytes may retain deleted text. Neither source revocation nor an FTS delete is secure physical erasure. The source-retention/backup policy and actual secure deletion are not resolved by this slice.

## Verification and remaining gates

The regression suite covers ranked search and provenance, bounded snippets/inputs, literal operator treatment, version changes before and after extraction, independent same-content source revocation, deleted index-row rebuild, repeated deterministic rebuild, byte/document budget failure, v4 migration backfill and backup/restore.

The FTS trigger and source filtering behavior was independently exercised against SQLite 3.46.1. **No .NET cross-platform CI pass may be inferred from that probe.** This ADR is not a benchmark or proof of cross-platform operational limits.

#106 stays open until the latest-head .NET CI/test/format checks, owner review and merge are verified. Integrated indexing/search UI and acceptance tests remain under #108/#130, relevance benchmarking under #29, and private retrieval authorization under #107/#117.
