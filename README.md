# Praxis

Praxis is a local-first knowledge and automation platform. Obsidian is the first editor, not a dependency of the data model, synchronization protocol, or services.

## Current state

The repository begins with a .NET 10 HTTP service, a liveness integration test, engineering standards, and a delivery roadmap. Identity, canonical records, synchronization, search, and agent execution are **not implemented**.

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

## Project direction

1. Preserve portable canonical records and original files.
2. Provide authorized, versioned sync with offline edits and explicit conflict handling.
3. Integrate Obsidian desktop and mobile as the initial clients.
4. Add capture, source-grounded retrieval, and constrained automation.
5. Offer independent first-party interfaces without migrating away from the underlying knowledge.

See [architecture](docs/architecture.md), [roadmap](docs/roadmap.md), [engineering workflow](docs/engineering.md), and [tracking conventions](docs/tracking.md). GitHub [roadmap issue #1](https://github.com/berniemackie97/Praxis/issues/1) is the work-item index.
