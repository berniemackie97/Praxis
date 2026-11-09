# ADR 0028: Responsibility-specific catalog stores over one serialized connection

**Status:** Accepted. Behavior-preserving restructuring; no schema change.

## Context

`LocalCatalog` had grown into a single partial class spread over 18 files: originals, sources, processing leases, text derivatives, search, topic proposals, collections, media evidence, export, recovery and snapshot I/O all shared private fields and helpers. File folders suggested separation that the type did not enforce. Every new processor or evidence kind was added as another partial, so any change could reach any table through the shared connection, and processing, enrichment and model-backed work planned for #115/#116/#31 would have grown it further.

## Decision

`LocalCatalog` is the session and lifetime owner only. It validates the private data directory, holds the exclusive `catalog.lock`, opens and migrates the database, and composes stores exposed as properties:

| Property | Type | Responsibility |
| --- | --- | --- |
| `Sources` | `LocalSources` | Path lookup, observed relocations, revocation |
| `Originals` | `LocalOriginals` | Idempotent capture, verification, export of immutable originals |
| `Library` | `LocalLibrary` | Latest-revision read model with processing state |
| `Processing` | `LocalProcessingQueue` | Leased jobs, attempt budgets, retry/cancel |
| `Text` | `LocalTextDerivatives` | Revision/digest-bound extracted text |
| `Search` | `LocalTextSearch` | Rebuildable FTS5 query and rebuild |
| `Topics` | `LocalTopicProposals` | Evidence-bound proposals and owner review |
| `Collections` | `LocalCollections` | Owner-authored collections and membership |
| `Media` | `LocalMediaEvidence` | Header inspection and digest-bound caption cues |
| `Recovery` | `LocalCatalogRecovery` | Original inventory inspection and live backup |

`LocalCatalogSnapshots` holds operations that need no open session (verify, staged restore, backup of a closed catalog). Processor identities live in `LocalProcessors`.

The internal `CatalogDatabase` is the only holder of the `SqliteConnection`. It exposes the connection solely through `Execute` callbacks guarded by a reentrant `System.Threading.Lock`, so no store can issue a statement outside serialized access, and a store may call another store from inside an operation. Multi-statement atomicity remains explicit: a store opens and commits its own transaction inside one `Execute` call. A cross-store write that must be atomic (capture scheduling its first processing job) uses an `internal static` helper that accepts the caller's connection and transaction rather than a second public entry point.

## Consequences

- Public API moved from `catalog.SearchText(...)` style to `catalog.Search.Query(...)`. Desktop protocol, worker, tools and tests were updated; wire contracts and SQL are unchanged.
- New capabilities add a new store with its own invariants instead of a partial; a store receives only the dependencies it uses (for example `LocalMediaEvidence` composes sources, search, text and the object store and never touches SQL).
- A single connection still serializes all catalog work. That is the existing, tested behavior and keeps transactional reasoning simple; long-running work (model inference, media decoding) must therefore happen outside `Execute` and commit results in short transactions with lease fencing. Introducing reader connections is a separate, measured decision.

## Rejected alternatives

- **Facade forwarding every method from `LocalCatalog`:** preserves call sites but recreates the god type as forwarding wrappers.
- **Repository interfaces per table:** single implementations with no substitution need; SQL invariants span tables (eligibility views, triggers), so table-shaped repositories would split logic that must stay together.
- **One connection per store:** breaks the single-writer assumption and the cross-store transactional capture path without measured benefit.
