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

## Release verification

The repository uses an exact .NET SDK and locked NuGet package graph. GitHub Actions verifies the backend build and tests on Linux, macOS and Windows; the Linux job additionally smokes the published API on loopback and checks a [declared-license/dependency inventory](docs/dependencies/README.md). See [release verification](docs/release-verification.md) for update commands, evidence and limitations. These checks do not imply that mobile clients or production synchronization are implemented.
