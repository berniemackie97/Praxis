# Praxis

Praxis is a local-first, independent Second Brain for capturing, preserving, finding, organizing and eventually using knowledge through explicitly authorized agents. **Obsidian is optional**: it is an editor adapter, not the primary application or required storage. The active delivery priority is a standalone local Inbox/Library/Search experience, followed by voice dictation, browser save, reviewable enrichment and scoped MCP agent access.

## Current state

The repository has an owner-local SQLite catalog, verified original-file preservation, revision-aware processing, bounded Markdown/text extraction, local FTS5 search and an automatically reconciling watched-folder worker. The worker is merged, and its source/test baseline was locally verified on macOS; the full watched-folder hardware acceptance is still open. The .NET API exposes liveness only and does not expose private knowledge over HTTP.

The [macOS workbench development slice](docs/delivery/macos-workbench.md) adds a first-party SwiftUI window that supervises that same worker through a private child-process pipe, with Inbox/Library/Search inspection and explicit source actions. **The workbench was merged without fresh native UI verification and is not yet released.** Source-backed URL capture, reviewable organization and enrichment, external agent grants, mobile clients and integrated first-run acceptance remain open. No cloud account or model provider is required for the local intake path.

## Product direction and delivery status

The [product experience](docs/product/second-brain-experience.md) defines the intended sources, original-versus-derived model, user-facing flows, microphone/browser capture and agent access. The [standalone local MVP plan](docs/delivery/standalone-mvp.md) defines a dependency-aware sequence of first useful slices and their acceptance criteria.

**Implemented on `main`:** portable note/attachment archives, optional Obsidian desktop interoperability, durable local catalog/originals, revision/receipt processing, bounded text derivatives, source-aware text search and a supervised automatic intake worker (merged PRs #123, #125, #132, #134, #135, #138, #140 and #144). **On `main`:** a macOS first-party application backed by the existing worker (merged PR #145, native smoke unverified). **On `main`:** persistent user-authored collections, source membership editing and filtered library navigation (merged PR #147; GUI acceptance still outstanding). **In the active draft:** trusted, keyboard-first quick note capture through the same preserved-original and indexing pipeline (#113). **Still open:** real UI acceptance (#108/#130), safe rename attribution (#105), physical recovery (#128), PDF/media processing (#103/#114), URL capture (#104), reviewed organization (#115/#116), scoped agents/MCP (#117–#119), remote sync and production release. Existing code and passing library tests are not proof of end-to-end release readiness.

## Development

Requires the .NET 10 SDK.

```sh
dotnet restore Praxis.slnx
dotnet build Praxis.slnx -c Release
dotnet test Praxis.slnx -c Release
dotnet format Praxis.slnx --verify-no-changes
dotnet run --project src/Praxis.Api
```

`GET /health/live` checks only that the process responds. It does not validate readiness, databases, or external services.

### Local note archive

`NoteRecord.Create(path, markdown)` creates a stable UUID and first revision. `record.Revise(newPath, markdown)` creates the next immutable revision, keeping the original snapshot. `NoteArchive.Write(stream, records)` exports a versioned ZIP with JSON manifest and separate Markdown revision entries; `NoteArchive.Read(stream)` validates and restores the records.

`AttachmentRecord.Create(path, bytes)` creates a stable binary identity; `Rename` preserves its original bytes and ID. A `KnowledgeCollection` combines note histories, binary attachments and explicit note/attachment references. `NoteArchive.WriteCollection` and `ReadCollection` round-trip a version-two archive containing all of them, while the version-one note-only APIs remain compatible.

The [note archive contract](docs/decisions/0004-portable-note-archive.md) and [attachment contract](docs/decisions/0005-attachment-collection-archive.md) document formats, integrity checks and capacity limits. These are local interchange operations, not an Obsidian vault adapter, a live backup service or multi-device synchronization.

### Local attachment transfer

`LocalAttachmentTransferStore` can resume a bounded on-disk upload using its transfer ID, expected SHA-256 and current offset. `Commit` publishes only verified original bytes under their digest; `IsCommitted` verifies a previously published blob after an ambiguous acknowledgment. The storage root is chosen and protected by the caller. This is local **plaintext staging**, not an authenticated network service or an encrypted backup. See [ADR 0007](docs/decisions/0007-local-attachment-staging.md) for failure handling, limits and unimplemented cleanup/retention requirements.

## Project direction

1. Preserve portable canonical records and recoverable original attachments.
2. Provide authorized, versioned sync with offline edits and explicit conflict handling.
3. Deliver an independent, local-only file/folder Inbox and first-party Praxis Library/Search interface—no Obsidian required.
4. Add intentional voice dictation, browser capture, source-grounded enrichment and scoped MCP access.
5. Validate remote sync, Obsidian desktop/mobile compatibility, broader clients and operational recovery separately, behind their security and device-test gates.

See [architecture](docs/architecture.md), [roadmap](docs/roadmap.md), [engineering workflow](docs/engineering.md), and [tracking conventions](docs/tracking.md). GitHub [roadmap issue #1](https://github.com/berniemackie97/Praxis/issues/1) is the work-item index.

## Offline note journal

`LocalNoteJournal` is a bounded, local-only checkpoint for multiple pending Markdown edits, conflict resolution, accepted-change receipts and a durable applied cursor. It survives ordinary process restarts and verifies echoed events without losing queued content. It is not encrypted storage, a complete vault backup or an authenticated synchronization service. See [ADR 0008](docs/decisions/0008-durable-offline-note-journal.md) for the owner-controlled directory, limits, and unimplemented integration gates.

## Local vault capture and restore

`LocalVaultExchange.CaptureArchive(sourceDirectory, archivePath)` captures a local Markdown folder, binary originals and hidden files into a validated collection ZIP. `LocalVaultExchange.RestoreArchive(archivePath, newDirectory)` restores the latest Markdown files and original attachments into a **new** directory, never overwriting a vault. Links are preserved as written; their meaning and target identity are not inferred. Empty nested directories, invalid UTF-8, and symbolic links fail explicitly.

Use the local command-line tool (requires the repository's exact .NET SDK):

```sh
dotnet run --project tools/Praxis.VaultCli -- capture /path/to/vault /path/to/new-backup.zip
dotnet run --project tools/Praxis.VaultCli -- verify /path/to/new-backup.zip
dotnet run --project tools/Praxis.VaultCli -- restore /path/to/new-backup.zip /path/to/new-restored-vault
dotnet run --project tools/Praxis.VaultCli -- compare /path/to/new-backup.zip /path/to/new-restored-vault
```

Paths must point to an existing source/parent directory; archive and restore destinations must not already exist. The read-only `compare` command reports current-file byte/path mismatches and fails on any missing, extra or changed file without modifying the archive or folder. It does not verify permissions, timestamps or empty directories. The ZIP includes original content and potentially private hidden configuration files, all **unencrypted**. This CLI does not modify an existing vault.

After the first archive, use an existing snapshot as the baseline for an identity-preserving incremental update:

```sh
dotnet run --project tools/Praxis.VaultCli -- plan /path/to/vault /backups/previous.zip
dotnet run --project tools/Praxis.VaultCli -- update /path/to/vault /backups/previous.zip /backups/next.zip
```

Unchanged records keep their IDs, changed Markdown appends history, updated binary attachments preserve attachment IDs, and existing explicit references remain attached to those IDs. **Missing prior files stop the update** rather than being silently discarded. Renames require an explicit JSON file passed as `--renames /path/to/mapping.json`; both input and output archives must live outside the vault. See [ADR 0010](docs/decisions/0010-incremental-vault-reconciliation.md) for the mapping format, rules and unresolved semantic-link/deletion limitations.

Before wiring Markdown embeds into stored identity references, inspect a captured or updated collection archive:

```sh
dotnet run --project tools/Praxis.VaultCli -- links /backups/next.zip
```

The read-only link audit resolves the supported local Markdown image and Obsidian embedded wikilink forms to attachment IDs. It reports missing, escaping and ambiguous targets with a nonzero exit code, without modifying the archive or source vault. This is **not** a complete Markdown/Obsidian parser: reference-definition links, advanced nested Markdown syntax, aliases, editor metadata and automatic link repair are not supported. The standalone `links` command stays read-only. Tracked capture and update optionally persist these **vault-derived** pairs separately from manually asserted references, using the backward-compatible [version-three collection archive](docs/decisions/0012-vault-reference-provenance.md). The scanner is intentionally incomplete: a successful audit is not proof that all Obsidian references resolve. See [ADR 0011](docs/decisions/0011-vault-attachment-link-audit.md).

To begin tracking supported embeds, capture with `--track-links`, or opt into tracking while updating an existing version-two archive. Once enabled, every subsequent update re-evaluates tracked references without repeating the option:

```sh
dotnet run --project tools/Praxis.VaultCli -- capture /path/to/vault /backups/first.zip --track-links
dotnet run --project tools/Praxis.VaultCli -- plan /path/to/vault /backups/first.zip
dotnet run --project tools/Praxis.VaultCli -- update /path/to/vault /backups/first.zip /backups/next.zip
```

Unresolved or ambiguous supported embeds block publication. Disappearing tracked links are listed by exact note/attachment IDs in a read-only `plan`, while explicit stored references remain untouched. To approve a removal, create a UTF-8 JSON file outside the vault containing an array of `{"noteId":"<uuid>","attachmentId":"<uuid>"}` pairs copied from the plan, then run `update ... --approve-unlinks /path/to/approved.json`. The approval must match the complete set of removed pairs; extra, duplicate or stale approvals fail without publishing. Paths and Markdown text are never rewritten. The scanner recognizes only the subset documented in [ADR 0011](docs/decisions/0011-vault-attachment-link-audit.md).

See [ADR 0009](docs/decisions/0009-local-vault-capture-restore.md) for size limits, filesystem failure boundaries and outstanding Obsidian integration work.

## Obsidian desktop adapter (local preview)

The first Obsidian desktop integration is under [`integrations/obsidian`](integrations/obsidian/README.md). It exposes explicit local archive capture/update and preview commands through Obsidian, tracking file renames into an external session ledger so the existing CLI can reconcile stable IDs. Its plugin manifest is desktop-only; it does not synchronize or rewrite notes. See [ADR 0013](docs/decisions/0013-obsidian-desktop-local-adapter.md).

The adapter has automated Node tests, but **real Obsidian desktop event and device validation has not been completed**. Encrypted backups, real-Obsidian folder-event acceptance, background synchronization and mobile support remain outstanding.

For a practical local trial using the current desktop adapter, see the [Mac first-run and restore checklist](integrations/obsidian/LOCAL-TRIAL.md). It uses a disposable Obsidian vault and actual .NET CLI preflight before installing a plugin, without enabling server synchronization or automatic knowledge classification.

## Release verification

The repository uses an exact .NET SDK and locked NuGet package graph. GitHub Actions verifies the backend build and tests on Linux, macOS and Windows; the Linux job additionally smokes the published API on loopback and checks a [declared-license/dependency inventory](docs/dependencies/README.md). See [release verification](docs/release-verification.md) for update commands, evidence and limitations. These checks do not imply that mobile clients or production synchronization are implemented.

## Owner-authored organization

First-use collections are persisted in the same source catalog (schema v6) and remain distinct from watched folder paths and machine-generated proposals. See the [macOS workbench verification guide](docs/delivery/macos-workbench.md) and [collection storage decision](docs/decisions/0023-owner-authored-collections.md). This implementation must pass migration, original-preservation and actual GUI tests before it is considered ready for use.

## Direct thoughts and paste capture

The first-party Quick note workflow (under [#113](https://github.com/berniemackie97/Praxis/issues/113)) saves an explicit authored Markdown note into the selected Inbox with exact-byte original preservation, idempotent receipt and automatic downstream indexing. This draft does not monitor or transmit clipboard contents, fetch pasted URLs or invoke any model. See [the owner-local workbench guide](docs/delivery/macos-workbench.md). Native GUI/build verification remains open.

 
## Reviewable enrichment (development)

Under #115, the proposal foundation stores source/revision/digest-bound literal Markdown-heading candidates and append-only owner review events in catalog schema v7. These are **not machine-understood topics or automatically accepted tags**. No model provider is required, and no external tool or agent receives new permissions. See [ADR 0024](docs/decisions/0024-reviewable-enrichment.md) and the [macOS workbench guide](docs/delivery/macos-workbench.md). Native acceptance and end-to-end release gates remain open.


## Exporting preserved originals (L0 development)

The first-party Library now has a draft owner-selected **Export original…** action for the exact active source version, backed by the content-addressed original store rather than the watched source file. It verifies SHA-256 while streaming to a new, non-overwriting destination. Supported and unsupported formats use the same path, and owner approval is required for each destination. This does not create a remote sharing API, an agent grant or a full catalog backup. See the [workbench smoke guide](docs/delivery/macos-workbench.md) and tracked [#166](https://github.com/berniemackie97/Praxis/issues/166). The new feature is not yet locally compiled/accepted.


## Observed file moves (development)

Praxis now has a draft opt-in-folder rename continuity implementation for already captured files. It preserves source identity only when a filesystem move event is independently verified against the current original digest and an unoccupied target; historical paths are recorded in schema v8. Copies and missed moves are not inferred as the same source. See [ADR 0025](docs/decisions/0025-file-source-relocations.md) for evidence limits, security and migration requirements. First-party and physical hardware acceptance remain open.

### Multidomain MVP direction

Praxis aims to answer questions and carry out authorized tasks across multiple domains. Creating a budget is one example of a future assistant request using permitted Second Brain sources and, where needed, scoped MCP tools. The current workbench supports digest-bound caption search and media-header inspection. Integrated assistant tasks and in-chat playback are tracked under [#170](https://github.com/berniemackie97/Praxis/issues/170) and [#177](https://github.com/berniemackie97/Praxis/issues/177).
