# ADR 0039: Layered modular monolith with persistence behind application ports

**Status:** Accepted. Behavior-preserving restructuring; no schema, wire-protocol or on-disk format change. Supersedes the project layout of [ADR 0031](0031-local-inference-boundary.md) and the store composition of [ADR 0028](0028-catalog-responsibility-stores.md).

## Context

`Praxis.Storage` had become the whole application: domain records, use cases (processing pipeline, enrichment, verification, retrieval fusion, media evidence), the desktop protocol and every SQL statement lived in one assembly, often in the same class. ADR 0028 split the catalog into responsibility stores, but each store still mixed validation and policy with SQL, model calls ran from inside storage types, the desktop protocol was one switch over the whole catalog returning anonymous objects, and the worker composed everything by hand. `Praxis.Storage` referenced `Praxis.Inference`, so persistence depended on model adapters. `Praxis.Knowledge` (portable notes) shared a name with the catalog's knowledge entity layer. Time was read from the wall clock in more than forty places, which forced test-only `nowUtc` parameters into production APIs.

## Decision

Praxis stays a single-process modular monolith with four layers. Dependencies point inward and are enforced by `tests/Praxis.Architecture.Tests`.

| Project | Owns | May reference |
| --- | --- | --- |
| `Praxis.Domain` | Records, statuses, limits and pure policies (provider routing, proposal and review rules, model profile normalization, collection names, capture scopes, embedding validation, processor stage graph) | Base class library only |
| `Praxis.Application` | Ports (`I*Repository`, `I*Store`, `I*Queue`, `IChatModel`, `IEmbeddingModel`, connectors), use cases (processing pipeline and handlers, enrichment, verification approval/configuration/runner, semantic indexing and retrieval, hybrid search, media evidence) and the desktop protocol with typed contracts | `Praxis.Domain` |
| `Praxis.Infrastructure` | SQLite persistence (catalog schema, migrations, repositories, backup and recovery sets, security registry and client trust store), the content-addressed original file store, filesystem intake adapters, Ollama and subscription CLI adapters | Application, Domain, `Microsoft.Data.Sqlite` |
| `Praxis.Worker`, `Praxis.Api` | Composition roots and hosting | Everything they compose |

`Praxis.Notes` (revisioned Markdown notes, attachment archives, the offline note journal, vault exchange and synchronization) remains independent of the catalog layers.

Rules that follow from the decision:

- **SQL lives only in `Infrastructure.Persistence`.** Repositories are feature-shaped, not table-shaped: a repository keeps the statements of one atomic operation together, including cross-table invariants (eligibility views, lease fencing, review carry-over), which is why ADR 0028 rejected table repositories.
- **Ports exist where the Application layer needs them.** Each repository implements the port its use cases call; query-only use cases call read ports directly instead of going through forwarding services.
- **Policy that must run inside a transaction is a domain function the repository calls.** Approval, which needs owner-reviewed payload narrowing and provider routing, runs outside the transaction and commits only if the request is still awaiting approval with the reviewed payload.
- **The desktop protocol is an application concern.** `DesktopProtocol` dispatches to responsibility-specific `IDesktopActionGroup`s; every response is a contract record whose JSON matches the previous wire format, and an optional `protocolVersion` other than 1 is refused.
- **The worker runs on the Generic Host with defaults disabled.** No configuration is read from environment variables or files. One supervisor runs intake, processing, verification and the stdio session as a group. Component status messages are logged through `Microsoft.Extensions.Logging` categories to a stderr formatter that keeps the one-line timestamped format and never writes exception details; process failures still print only a fixed category.
- **Time comes from an injected `TimeProvider`.** Tests drive lease expiry and day boundaries with a manual clock.
- **Pure queries may use a read-only connection.** The catalog already runs SQLite in WAL mode with one serialized writer. Owner queries now read the last committed state through a second, read-only connection instead of waiting behind background processing; a query issued inside a write on the same thread still uses the writer connection.
- **One top-level type per file, named after the file, in a namespace that follows its folder.** The redundant `Local` prefix is gone from component names; it remains where it means device-local as opposed to remote (`LocalNoteState`) or local models (`LocalEnrichment`).

## Consequences

- Public names changed: `LocalCatalog` → `SqliteCatalog`; stores → `Sqlite*Repository`/`SqliteProcessingQueue`/`SqliteOriginalStore`/`SqliteTextSearchIndex`/`SqliteSemanticVectorStore`; `LocalProcessingPipeline` → `ProcessingPipeline`; intake adapters → `FileSourceIntake`, `WatchedFolderIntake`, `WatchedFolderIntakeGroup`, `QuickNoteCapture`; `LocalDesktopProtocol` → `DesktopProtocol`; `Praxis.Knowledge` → `Praxis.Notes`; `tools/Praxis.Storage.RecoveryCheck` → `tools/Praxis.RecoveryCheck`. Embedded migrations are `Catalog.V1.sql` … `Catalog.V16.sql`.
- The Xcode worker publisher verifies the four distinct assemblies `Praxis.Domain`, `Praxis.Application`, `Praxis.Infrastructure` and `Praxis.Worker`.
- The security registry keeps verify-and-consume transactions beside the signature and grant checks they protect. It has moved under `Infrastructure.Persistence.Security`, but separating its cryptographic checks from SQL needs its own security review before remote trust (#50) is enabled.
- The read-only connection is a second SQLite handle in the same process; the exclusive `catalog.lock` still prevents a second writer process.

## Rejected alternatives

- **Dapper for row mapping:** SQLite returns `INTEGER` as `Int64`, which breaks Dapper's constructor mapping for records with `int` members; adapting every record would add row types without a measured benefit (R08).
- **Asynchronous ADO.NET:** Microsoft.Data.Sqlite executes its async methods synchronously, so they would add allocation without releasing threads.
- **A connection per repository or unit of work for writes:** breaks the single-writer assumption and cross-repository atomic capture without a measured benefit; only pure queries use the read connection.
- **Keeping one storage assembly with folders:** folders did not stop any class from reaching SQL, model runtimes or the protocol, which is the coupling this decision removes.
