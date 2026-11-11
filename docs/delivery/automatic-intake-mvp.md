# Praxis owner-local automatic intake checkpoint

**Status:** Multi-root and reapproval handling are merged through #261. The layered backend refactor in #262 changed module boundaries without altering intake's on-disk contract. The previous 672-test owner-Mac verification predates both slices; #262 reports 725 passing local tests but CI and physical watcher acceptance remain independent gates. The in-review #105 intake-health slice adds durable extraction backlog visibility and scan progress, subject to fresh owner-machine verification.

## What runs now

The worker supervises **one to four explicitly approved local intake folders**. The primary folder is also the Quick note publication destination. Each root has its own file watcher, rename evidence and status; all share the same catalog, idempotent capture/processing pipeline and a serialized hashing/scanning lane. Folder choices are made through the first-party app, not through a remote API. The worker does not listen on HTTP, request network credentials, access browser profiles, invoke a remote model, or require Obsidian. New files in an available approved folder do not require a manual import command.

The worker reuses the catalog's original-store capture, deterministic source identities, durable processing jobs, versioned UTF-8 text/Markdown extraction, and derived FTS5 index. A user can safely keep source files in place; Praxis never moves or deletes them.

The pipeline is:

1. Reconcile the selected directory at startup and at regular intervals. `FileSystemWatcher` notifications accelerate reconciliation but are not the source of truth. An overflow or missed event cannot permanently suppress periodic discovery.
2. Validate bounded directory entries using `FileSourceIntake`'s existing traversal. Symbolic links, paths into the Praxis data root, exceeded depth/count limits, and missing/unreadable input are rejected rather than followed.
3. Require a stable file size and last-modified timestamp across scans, then stream a SHA-256 digest before capture. Files are imported only when bytes differ from the latest preserved version. A request key binds the expected revision and digest for crash/retry idempotence.
4. Preserve the original bytes and processing receipt in the owner-private catalog. Captures wake the separate processing pipeline (ADR 0029), which runs leased jobs with bounded per-processor concurrency; a supported extraction is searchable through the existing FTS5 index. Other formats are retained with explicit unsupported outcomes. Latest-revision and source-revocation constraints apply to search.
5. On restart, rescan and rehash. An already committed digest is not recopied or given another logical revision. Deleted/disconnected paths do **not** revoke or destroy previous captured material; explicit retention/removal is a separate owner decision.

Current bounded defaults **per root**: 5,000 files, 12 directory levels, 2 GiB per original, 256 GiB inspected input, 2 GiB source hashing budget per scan, three-second quiet period, ten-second periodic rescan, and rehash unchanged metadata after thirty minutes. At most four roots may be approved; their scans share a single lane rather than running concurrent high-I/O hashes. The global pause/retry actions fan out to each watcher; health remains per-root. `.part`, `.tmp`, incomplete browser downloads, common editor swaps, and macOS `.DS_Store` are ignored until an ordinary filename appears.

The file-size/mtime stability window handles common file copies and atomic replacements but cannot prove a producer has permanently closed a file during a prolonged writing pause, especially on Unix. Changing bytes after capture produces a later revision; the worker flags detected capture-time mutations for rescan rather than accepting a mismatched hash. Observed in-scope file or directory renames preserve source identity only after matching each prior original digest and verifying an unoccupied destination. Missed notifications and copies never infer identity from matching content alone. Notifications invalidate metadata-based verification caches; a watcher error or reconnect forces the next passes to recheck all paths. Pending invalidations are bounded to 512 paths, with overflow invalidating the entire cache, and notification-driven scan starts are spaced at least 250 ms apart. After detecting new or changed metadata, the worker schedules a bounded follow-up at the quiet-period deadline instead of waiting for a full periodic interval. Source bytes beyond the per-file cap produce an owner-visible error without attempting to hash the file.

## Headless local run (the first-party macOS workbench is tracked separately)

Create a separate private catalog directory and an explicitly selected Inbox once. On macOS:

```sh
mkdir -p "$HOME/PraxisInbox"
mkdir -m 700 "$HOME/PraxisData"
cd /path/to/Praxis

dotnet restore Praxis.slnx --locked-mode
dotnet run --project src/Praxis.Worker -c Release -- \
  --data-root "$HOME/PraxisData" \
  --intake "$HOME/PraxisInbox" \
  --intake "/absolute/second-approved-folder"
```

The extra `--intake` argument is optional; the existing single-folder invocation still works. A headless invocation is an explicit owner decision to use all paths supplied on that command line. Roots cannot overlap each other or the catalog; the worker refuses more than four.

If the data directory already exists, do not blindly change its permissions or replace its contents. Praxis validates private root permissions before opening. The root and watched directory must be **separate**; do not point the watched directory at the catalog, a backup, or an ancestor containing them. Use only disks and directories you intend to trust.

If an approved folder disappears or its creation identity changes, that watcher remains **offline** or **reapproval_required** and does not scan the returned path until it is reapproved. Canonical originals are retained and the other roots keep working; previously queued processing continues. The desktop UI records device and file identities when folders are selected. On reconnect it may reapprove a root automatically **only** when the same saved filesystem identity is still present; otherwise the owner must explicitly choose the folder again. The worker also rejects automatic scanning after an observed disappearance, even when the path text matches. Headless workers do not have a folder picker: deliberately stop/restart with the intended `--intake` path to reapprove it. These checks protect against ordinary remount/replacement mistakes, but they are not cryptographic authentication or a substitute for OS-level mount isolation.

With that **single running process**, copy Markdown, text, and arbitrary other files into one or more selected folders. The worker writes fixed-category operational status to stderr; no terminal command is needed for each import. Use Ctrl-C to stop cleanly. The SwiftUI workbench is a first-party Xcode app; the headless invocation is not an installer, login item or tray application.

For current search and library inspection, `SqliteCatalog.Search.Query` and `SqliteCatalog.Library.List` are owner-local library APIs. The SwiftUI workbench presents source browsing, search, per-root intake status, global pause/retry, and the processing backlog of queued/running/failed jobs for current revisions of active sources. The backlog and recent significant watcher incidents are read from durable catalog state. The incident journal stores at most 512 recent transitions with root path, fixed state and start/resolution timestamps; per-file error details and captured-since-launch counters remain session-local. Incident entries do not authorize source revocation or deletion. Actual Xcode application launch and first-run acceptance remain open under #215/#130. There is intentionally no unauthenticated private-data HTTP endpoint.

## Remaining #105 / #130 acceptance

Run the existing and new xUnit suites, including:

- New text and unsupported binary become separate verified original objects with correct source IDs and extraction outcomes; supported content becomes searchable.
- An updated file keeps item/source identity, advances the revision exactly once, and retracts obsolete search results.
- Atomic rename of a temporary download into its final name is ingested; temporary files are skipped.
- Cancellation/restart and unchanged-file rescans do not create duplicate item versions or committed receipts.
- Symlinks and out-of-scope data roots fail closed; actual disconnected volumes and read errors do not implicitly delete sources.
- During a real macOS session, dropping files while Praxis runs is automatically reflected without any manual trigger. Inspect logs and stable on-disk catalog records.

Still required for release: verification of current layered `main` and this intake-health change on the owner Mac, real Xcode selection/reconnect proof, actual disk-full/unplug/overflow fault evidence, cross-platform checks and complete first-party acceptance. Folder-set edits currently require stopping and restarting the worker; login-start remains a separately opted-in OS lifecycle feature, not a default. The implemented verified-rename path must not be confused with proof of missed-event directory move identity. Track the outstanding work in #105/#130/#215 and their related features.

**Format boundary:** `.md`, `.markdown`, and `.txt` UTF-8 files up to 1 MiB currently receive extracted searchable text. PDF text extraction, image understanding, audio transcription, archives, links and full knowledge enrichment are **not yet implemented** by this worker; they remain preserved originals or future explicit source types. Any entity/tag/summary/relation generated later must retain processor/source provenance and a review status (#115/#116). No local LLM process or MCP grants are silently enabled.

## Owner-machine fault acceptance

Use a separate owner-approved scratch Inbox and private catalog. With the actual macOS application or headless worker running, capture a small text file and verify search. Replace it through an ordinary-name atomic rename; modify it in place; repeat with a same-size file whose timestamp is restored; stop and restart during capture; move the Inbox out of reach and restore it. Verify retained original identities/revisions, error and offline states, and no source deletion. Record the worker/app version and commit SHA, timestamps, observed status, original verification, test exit codes, and whether filesystem notifications were actually delivered. Do not classify a polling-only success as notification proof.

Exercise unreadable files, permissions, actual unplug/replug, and disk-full on an explicitly disposable test volume; stop before risking owner data. Watcher buffer overflow requires controlled injection or reliable event evidence. A directory remounted at the same path is not authenticated by its spelling. The first-party app compares saved local device+inode identity before reapproval; a changed identifier requires an explicit fresh folder selection. Unit tests cannot substitute for a real remove/remount sequence. Follow [the physical watched-intake acceptance checklist](../testing/watched-intake-acceptance.md) and record the exact tested commit. Full physical fault coverage remains open under #105. The unit tests simulate a watcher error but do not prove operating-system overflow delivery.

## Delivery and CI constraints

Project #3 tracks #105 as P1/L0. #101 is closed; #105 remains In Progress until the physical watcher/volume and integrated first-party acceptance gates pass. #130 depends on #105 and #215. The trusted owner-local worker does not create an HTTP authorization bypass.

The GitHub Actions workflow is presently configured for self-hosted Linux/macOS/Windows workers, but the owner has explicitly deferred runner provisioning and has not approved billed compute (#137). A queued check is **not** evidence of successful tests. The branch must receive explicit local verification, review, and eventually the required cross-platform execution before claims of production/release completion. This change upgrades catalogs to **schema v17** for the private incident journal. Create and verify a coherent backup before opening valuable existing catalogs with the new worker; older binaries cannot reopen an upgraded catalog. Its migration, backup/restore, size bound and fixed-state privacy constraints are documented in [ADR 0040](../decisions/0040-watched-intake-incident-journal.md).
