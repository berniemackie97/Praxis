# Praxis

Praxis is a local-first, independent Second Brain for preserving and using owner-approved knowledge. Obsidian is an optional editor and derived-knowledge destination, not Praxis's canonical store. The first product MVP is an **integrated, source-grounded, permissioned assistant**; a working Inbox/Library/Search app is its necessary foundation, not the release itself.

## Current state

**Merged on `main`:** a private owner-local SQLite catalog; verified immutable originals and revision/receipt history; supervised watched-folder intake with source-rename and path-alias handling; bounded text extraction and segmentation; FTS5 search; owner-authored collections and quick-note capture; original export; and explicit verified catalog backup/restore commands. Unsupported formats remain preserved without fabricated extraction.

The first-party SwiftUI workbench is now a **checked-in Xcode macOS application**. It supervises the .NET 10 worker through private inherited pipes and exposes Inbox, Library, Search, Knowledge, Verification and Models workflows. The application build publishes the .NET worker into its bundle. Merged PRs #216–#220 address Xcode packaging, SwiftUI compilation and assembly-name collisions; **an actual successful owner-Mac Xcode build, window launch, worker session and first-run acceptance are still outstanding** (#215/#130).

**Intelligence now implemented in bounded slices:** a loopback-only Ollama inference adapter; versioned, quote-checked topic/entity/relation proposals; cross-source entity identities and owner corrections; instruction-like evidence withholding; and owner-approved Codex/Claude CLI verification with inspectable, redactable requests. These are not evidence of perfect factual classification, unrestricted subscription APIs, general-purpose agent chat or autonomous tool authority. Source capture/search does not require a model. The .NET API currently exposes liveness rather than private knowledge over HTTP.

## Product direction and delivery status

The [product experience](docs/product/second-brain-experience.md) defines the intended first release; [foundation and intelligent MVP delivery](docs/delivery/standalone-mvp.md) tracks the independent acceptance gates.

**Foundation #130 remains open** for verified macOS first run, mixed-source lifecycle, original recovery, denial/security and real device behavior. **Intelligent MVP #170 remains open** for evaluated cited technical answers, playable source-linked video excerpts, reconciled editable budgeting through the assistant, governed agent tasks, safe Obsidian knowledge projections and measured routing. Initial caption evidence, media-header inspection and single-source financial analysis are not substitutes for those complete workflows. URL/browser capture, speech, agent/MCP access, remote sync and production distribution remain separately gated.

**Verification status:** targeted backend and model runs are documented in individual PRs; no current all-platform GitHub Actions success can be claimed while #137 has no provisioned runners. Merged code, local test evidence, first-party hardware acceptance and release readiness are distinct statuses. [Project #3](https://github.com/users/berniemackie97/projects/3) is the delivery board, with the [repository issue plan](scripts/github/roadmap.json) as its versioned reconciliation input.

## Development

Requires the .NET 10 SDK.

```sh
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release
dotnet test Praxis.slnx -c Release
dotnet format Praxis.slnx --verify-no-changes
dotnet run --project src/Praxis.Api
```

`GET /health/live` checks only that the process responds. It does not validate readiness, databases, or external services.

### Native macOS app in Xcode

The macOS front end is a **version-controlled Xcode Application target**, not a Swift Package executable. Only the app and native tests are Swift; the canonical intake, storage, inference, knowledge processing and desktop worker remain **.NET 10**. Open `apps/macos/Praxis.xcodeproj`, select the shared **Praxis** scheme and **My Mac**, then use **Run (⌘R)** or **Test (⌘U)**.

The Xcode app target publishes the .NET worker into `Praxis.app/Contents/Resources/worker` as part of its build. A local .NET 10 SDK is required **to build**, and the framework-dependent worker requires a .NET 10 runtime **to run**. If Finder-launched Xcode cannot locate your SDK, set the user-defined target build setting `PRAXIS_DOTNET_EXECUTABLE` to its absolute path. The first-run UI separately locates the installed runtime used to execute the worker. No .NET application is rewritten in Swift.

For a command-line build of the **same Xcode app**, use `bash scripts/macos/build-desktop.sh`. To run the hosted Swift tests, including the published worker connection, use `bash scripts/macos/test-worker-session.sh` after packaging. Existing `.artifacts/Praxis.app` is intentionally not overwritten; move it aside before rebuilding. See [the Xcode workflow and acceptance guide](docs/delivery/macos-workbench.md) and [ADR 0036](docs/decisions/0036-native-macos-xcode-application.md).

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

## Local catalog backup and restore

Use the [offline recovery commands](docs/delivery/macos-workbench.md#offline-catalog-recovery) to back up the Praxis catalog and preserved originals together, verify the snapshot, and restore into a new directory.

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

The repository uses an exact .NET SDK and locked NuGet package graph. GitHub Actions is configured for Linux, macOS and Windows backend verification; the Linux workflow also checks API liveness and the [dependency inventory](docs/dependencies/README.md). **Current workflows are queued without eligible runners (#137), not verified green.** See [release verification](docs/release-verification.md) for commands, evidence and release limitations.

## Owner-authored organization

First-use collections are persisted in the same source catalog (schema v6) and remain distinct from watched folder paths and machine-generated proposals. See the [macOS workbench verification guide](docs/delivery/macos-workbench.md) and [collection storage decision](docs/decisions/0023-owner-authored-collections.md). This implementation must pass migration, original-preservation and actual GUI tests before it is considered ready for use.

## Direct thoughts and paste capture

Merged #148 implements first-party Quick note capture (#113) through the existing authorized Inbox/original/indexing path. It writes an explicitly authored Markdown note with an idempotent receipt; intentional paste is not a background clipboard watcher, and a pasted URL is not automatically fetched. #113 remains open for actual keyboard, failure, duplicate/restart and native-app acceptance.

## Reviewable enrichment (development)

The heading-only schema-v7 proposal lifecycle (#165) is a historical foundation. Merged #205–#212 add loopback Ollama, versioned quoted topic/entity/relation proposals, cross-source entity identities, owner correction, flagged-evidence withholding and approved redactable stronger-model CLI verification. These remain **unverified inferred knowledge** until separately reviewed or corroborated; a quote is not proof of truth. #115/#116/#31 remain open for quality, revocation and current-app acceptance. No external agent or model acquires new rights from imported text.

## Exporting preserved originals (L0 development)

Merged #167/#192 implement owner-selected, integrity-verified **Export original…** for the active source version, independent of the watched input file. Output is staged and published without overwriting an existing destination, and source revocation invalidates access. The path does not authorize remote/agent reads or replace coherent catalog backup. #166 remains open for current native Save dialog, physical/symlinked destinations and race/security acceptance.

## Observed file moves (development)

Merged #169 preserves stable source identity for verified observed file renames inside the approved Inbox, and #213 canonicalizes alternate path spellings. Copies or unobserved moves are not guessed to be the same source; prior locators remain traceable. Physical watch notifications, unplug/reattach, missed events, restart and security acceptance are still open under #105/#130.

### Multidomain MVP direction

Praxis aims to answer questions and carry out authorized tasks across multiple domains. Creating a budget is one example of a future assistant request using permitted Second Brain sources and, where needed, scoped MCP tools. The current workbench supports digest-bound caption search and media-header inspection. Integrated assistant tasks and in-chat playback are tracked under [#170](https://github.com/berniemackie97/Praxis/issues/170) and [#177](https://github.com/berniemackie97/Praxis/issues/177).
