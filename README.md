# Praxis

Praxis is a local-first knowledge and automation platform. Obsidian is the first editor, not a dependency of the data model, synchronization protocol, or services.

## Current state

The repository contains a .NET 10 HTTP service with process liveness checks and a separate `Praxis.Knowledge` library for portable Markdown note identities, immutable revision histories and validated local archive import/export.

Authentication, persistent records, synchronization, attachments, search and agent execution are **not implemented**. The archive library does not send data to any service.

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

The [archive contract](docs/decisions/0004-portable-note-archive.md) documents its limits, integrity checks and compatibility rules. It does not replace an Obsidian vault, synchronize devices, or include attachments yet.

## Project direction

1. Preserve portable canonical records and recoverable original attachments.
2. Provide authorized, versioned sync with offline edits and explicit conflict handling.
3. Integrate Obsidian desktop and mobile as the initial clients.
4. Add capture, source-grounded retrieval, and constrained automation.
5. Offer independent first-party interfaces without migrating away from the underlying knowledge.

See [architecture](docs/architecture.md), [roadmap](docs/roadmap.md), [engineering workflow](docs/engineering.md), and [tracking conventions](docs/tracking.md). GitHub [roadmap issue #1](https://github.com/berniemackie97/Praxis/issues/1) is the work-item index.
