# Praxis

Praxis is a local-first knowledge and automation platform. Obsidian is the first editor, not a dependency of the data model, synchronization protocol, or services.

## Current state

The repository contains a .NET 10 HTTP service with process liveness checks and a separate `Praxis.Knowledge` library for portable Markdown note identities, immutable revision histories and validated local archive import/export.

Authentication, durable canonical collection storage, synchronization, search and agent execution are **not implemented**. Local archives and optional staged attachment files do not send data to any service.

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
3. Integrate Obsidian desktop and mobile as the initial clients.
4. Add capture, source-grounded retrieval, and constrained automation.
5. Offer independent first-party interfaces without migrating away from the underlying knowledge.

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
```

Paths must point to an existing source/parent directory; archive and restore destinations must not already exist. The ZIP includes original content and potentially private hidden configuration files, all **unencrypted**. This CLI does not modify an existing vault.

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

## Release verification

The repository uses an exact .NET SDK and locked NuGet package graph. GitHub Actions verifies the backend build and tests on Linux, macOS and Windows; the Linux job additionally smokes the published API on loopback and checks a [declared-license/dependency inventory](docs/dependencies/README.md). See [release verification](docs/release-verification.md) for update commands, evidence and limitations. These checks do not imply that mobile clients or production synchronization are implemented.
