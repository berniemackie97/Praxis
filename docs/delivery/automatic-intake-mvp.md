# Praxis owner-local automatic intake checkpoint

**Status:** Bounded owner-local watcher and first-party configuration flows exist. This checkpoint is not release certification: clean builds, cross-platform CI, physical removable-volume/fault tests, and first-run macOS GUI acceptance still require evidence.

## What runs now

The proposed `Praxis.Worker` process supervises one explicitly approved owner-local intake directory. It does not listen on HTTP, request network credentials, access browser profiles, invoke a remote model, or require Obsidian. Once the process is running, adding a file to that directory **does not require another command or a manual processing trigger**.

The worker reuses the catalog's original-store capture, deterministic source identities, durable processing jobs, versioned UTF-8 text/Markdown extraction, and derived FTS5 index. A user can safely keep source files in place; Praxis never moves or deletes them.

The pipeline is:

1. Reconcile the selected directory at startup and at regular intervals. `FileSystemWatcher` notifications accelerate reconciliation but are not the source of truth. An overflow or missed event cannot permanently suppress periodic discovery.
2. Validate bounded directory entries using `LocalSourceIntake`'s existing traversal. Symbolic links, paths into the Praxis data root, exceeded depth/count limits, and missing/unreadable input are rejected rather than followed.
3. Require a stable file size and last-modified timestamp across scans, then stream a SHA-256 digest before capture. Files are imported only when bytes differ from the latest preserved version. A request key binds the expected revision and digest for crash/retry idempotence.
4. Preserve the original bytes and processing receipt in the owner-private catalog. Captures wake the separate processing pipeline (ADR 0029), which runs leased jobs with bounded per-processor concurrency; a supported extraction is searchable through the existing FTS5 index. Other formats are retained with explicit unsupported outcomes. Latest-revision and source-revocation constraints apply to search.
5. On restart, rescan and rehash. An already committed digest is not recopied or given another logical revision. Deleted/disconnected paths do **not** revoke or destroy previous captured material; explicit retention/removal is a separate owner decision.

Current bounded defaults: **5,000 files**, **12 directory levels**, **2 GiB per original**, **256 GiB total inspected input**, **2 GiB source hashing budget per scan**, **3-second quiet period**, **10-second periodic rescan**, and rehash unchanged metadata after thirty minutes. `.part`, `.tmp`, incomplete browser downloads, common editor swaps, and macOS `.DS_Store` are ignored until an ordinary filename appears.

The file-size/mtime stability window handles common file copies and atomic replacements but cannot prove a producer has permanently closed a file during a prolonged writing pause, especially on Unix. Changing bytes after capture produces a later revision; the worker flags detected capture-time mutations for rescan rather than accepting a mismatched hash. Observed in-scope file renames preserve source identity only after matching the prior original digest and verifying an unoccupied destination. Missed notifications and copies never infer identity from matching content alone. Notifications invalidate metadata-based verification caches; a watcher error or reconnect forces the next passes to recheck all paths. Pending invalidations are bounded to 512 paths, with overflow invalidating the entire cache, and notification-driven scan starts are spaced at least 250 ms apart. Source bytes beyond the per-file cap produce an owner-visible error without attempting to hash the file.

## Headless local run (the first-party macOS workbench is tracked separately)

Create a separate private catalog directory and an explicitly selected Inbox once. On macOS:

```sh
mkdir -p "$HOME/PraxisInbox"
mkdir -m 700 "$HOME/PraxisData"
cd /path/to/Praxis

dotnet restore Praxis.slnx --locked-mode
dotnet run --project src/Praxis.Worker -c Release -- \
  --data-root "$HOME/PraxisData" \
  --intake "$HOME/PraxisInbox"
```

If the data directory already exists, do not blindly change its permissions or replace its contents. Praxis validates private root permissions before opening. The root and watched directory must be **separate**; do not point the watched directory at the catalog, a backup, or an ancestor containing them. Use only disks and directories you intend to trust.

If an explicitly selected external intake folder is temporarily unavailable at startup or disconnects later, the worker **waits without creating a substitute folder or revoking source data**. It continues draining previously committed processing jobs and reattaches its watcher after the path returns. The operator must ensure the reattached path is the intended trusted volume, not an unrelated directory mounted at the same location.

With that **single running process**, copy Markdown, text, and arbitrary other files into `$HOME/PraxisInbox`. The worker writes local capture and error status to its console; no terminal command is needed for each import. Use Ctrl-C to stop cleanly. Running the worker is not yet an installer, login item, tray application, or first-party GUI.

For current search and library inspection, `LocalCatalog.Search.Query` and `LocalCatalog.Library.List` are owner-local library APIs. The repository now contains a first-party SwiftUI workbench with source browsing, search and intake status/pause/retry controls. Actual Xcode application launch and first-run acceptance remain open under #215/#130. There is intentionally no unauthenticated private-data HTTP endpoint.

## Acceptance to complete #101 / #105 / #130

Run the existing and new xUnit suites, including:

- New text and unsupported binary become separate verified original objects with correct source IDs and extraction outcomes; supported content becomes searchable.
- An updated file keeps item/source identity, advances the revision exactly once, and retracts obsolete search results.
- Atomic rename of a temporary download into its final name is ingested; temporary files are skipped.
- Cancellation/restart and unchanged-file rescans do not create duplicate item versions or committed receipts.
- Symlinks and out-of-scope data roots fail closed; actual disconnected volumes and read errors do not implicitly delete sources.
- During a real macOS session, dropping files while Praxis runs is automatically reflected without any manual trigger. Inspect logs and stable on-disk catalog records.

Still required for release: supervised OS-login lifecycle and owner-configured watch roots via the first-party UI, safe rename/move source attribution, distinguishable missing/disconnected states, actual disk-full and failure injection, cross-platform tests, complete source-aware browsing, source-backed URL bookmarks, and explicit owner-controlled organization. These remain tracked in #101/#105/#104/#108/#130 rather than silently declared complete.

**Format boundary:** `.md`, `.markdown`, and `.txt` UTF-8 files up to 1 MiB currently receive extracted searchable text. PDF text extraction, image understanding, audio transcription, archives, links and full knowledge enrichment are **not yet implemented** by this worker; they remain preserved originals or future explicit source types. Any entity/tag/summary/relation generated later must retain processor/source provenance and a review status (#115/#116). No local LLM process or MCP grants are silently enabled.

## Owner-machine fault acceptance

Use a separate owner-approved scratch Inbox and private catalog. With the actual macOS application or headless worker running, capture a small text file and verify search. Replace it through an ordinary-name atomic rename; modify it in place; repeat with a same-size file whose timestamp is restored; stop and restart during capture; move the Inbox out of reach and restore it. Verify retained original identities/revisions, error and offline states, and no source deletion. Record the worker/app version and commit SHA, timestamps, observed status, original verification, test exit codes, and whether filesystem notifications were actually delivered. Do not classify a polling-only success as notification proof.

Exercise unreadable files, permissions, actual unplug/replug, and disk-full on an explicitly disposable test volume; stop before risking owner data. Watcher buffer overflow requires controlled injection or reliable event evidence. A directory remounted at the same path is not authenticated as the same volume: the operator must verify the trusted volume before resuming intake. Multi-root management, volume identity, and full physical fault coverage remain open under #105. The unit tests simulate a watcher error but do not prove operating-system overflow delivery.

## Delivery and CI constraints

Project #3 tracks #105 as P1/L0. #101 is closed; #105 remains In Progress until the physical watcher/volume and integrated first-party acceptance gates pass. #130 depends on #105 and #215. The trusted owner-local worker does not create an HTTP authorization bypass.

The GitHub Actions workflow is presently configured for self-hosted Linux/macOS/Windows workers, but the owner has explicitly deferred runner provisioning and has not approved billed compute (#137). A queued check is **not** evidence of successful tests. The branch must receive explicit local verification, review, and eventually the required cross-platform execution before claims of production/release completion.
