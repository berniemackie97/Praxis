# Owner-local macOS knowledge workbench

**Status (November 10, 2026):** The version-controlled Xcode macOS app, worker protocol, watched intake, collections, original export and Knowledge/Verification/Models interfaces are merged (#145–#220). Backend and selected offscreen UI checks exist, but a successful build, launch and first-run acceptance of the current native app on the owner's Mac are still open (#215/#130). GitHub Actions has no eligible runners (#137); this is not a signed or released product.

## Trust and lifecycle

The first-party window launches one .NET worker as a private child process. Structured messages travel over its inherited stdin/stdout pipes. Only that worker opens the catalog and owns the existing watched intake, processing leases, verified originals and FTS5 index. No TCP listener, browser route, external inference, MCP grant or agent permission is enabled.

The private pipe supports bounded library pages, local lexical search, versioned extracted-text previews, integrity verification, processing retry and explicit source revocation. The UI asks for confirmation before revocation. Revocation removes a source from active retrieval; it does **not** automatically destroy an original. Original export is implemented (#167/#192); current-app Save dialog, TOCTOU/security and real-volume acceptance remain open under #166.

The first macOS delivery surface does not settle #36's future Windows/Linux/mobile stack decision. The app requires the .NET 10 runtime and, during development, full Xcode (Swift 6.2 or later) as the active developer directory; the standalone Command Line Tools lack Swift Testing and are rejected by the build scripts. macOS GUI processes may not inherit a shell's dotnet path; first-run setup can explicitly select a trusted dotnet executable when it is installed outside standard locations.

## Build and first-use smoke

From the repository root:

```sh
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
bash scripts/macos/build-desktop.sh
bash scripts/macos/test-worker-session.sh
open .artifacts/Praxis.app
```

The canonical macOS target is `apps/macos/Praxis.xcodeproj`, with a **shared Praxis scheme** for the native app, debugger, unit tests and profiling. Open that project in Xcode, choose **Praxis → My Mac**, and use **⌘R** to run or **⌘U** to test. `Sources/Praxis` contains only the Swift/SwiftUI front end; `Tests/PraxisTests` contains Swift Testing suites hosted by the app. The old `Package.swift` executable entry point has been removed to avoid Xcode launching a bare Mach-O executable without a bundle identifier.

The Xcode application target produces a real `.app` with a generated `Info.plist`, fixed development identifier `dev.praxis.renames-preview`, macOS 13 deployment floor and locally signed app for the debugger. Its Run Script build phase executes `scripts/macos/publish-worker.sh` **before Xcode's final app signing step**. That script uses the existing .NET 10 project to publish the backend into `Contents/Resources/worker` and never modifies the private catalog. The backend remains .NET; none of the processing, databases, models or storage are copied into Swift. App-hosted tests skip automatic opening of the owner's saved catalog and check the actual bundle plus worker location.

### Diagnosing Xcode worker build failures

Xcode's Run Script phase inherits Xcode build settings as process environment variables. MSBuild also reads environment variables as project properties. The root `Directory.Build.props` therefore explicitly pins each .NET project's `AssemblyName` and `TargetName` to its own `MSBuildProjectName` so `Praxis.Inference`, `Praxis.Storage` and `Praxis.Worker` cannot all emit `Praxis.dll`. The Xcode worker publisher validates each evaluated `TargetFileName` before building and verifies that all three distinct assemblies appear in the published bundle.

If Xcode reports `Command PhaseScriptExecution failed`, expand **Publish .NET Worker** in the Xcode Report Navigator. Fix the first underlying error rather than removing the phase or editing the Swift application. Confirm the distinct names independently with:

```sh
dotnet msbuild src/Praxis.Inference/Praxis.Inference.csproj -getProperty:TargetFileName
dotnet msbuild src/Praxis.Storage/Praxis.Storage.csproj -getProperty:TargetFileName
dotnet msbuild src/Praxis.Worker/Praxis.Worker.csproj -getProperty:TargetFileName
```

These should report `Praxis.Inference.dll`, `Praxis.Storage.dll` and `Praxis.Worker.dll` respectively. The Xcode build still requires the local .NET SDK and performs a locked restore and worker publish; no backend code is translated into Swift.

The worker is **framework-dependent in this development slice**: a local .NET 10 SDK is needed at build time, and a .NET 10 runtime is needed on the Mac at run time. Finder-launched Xcode may not inherit your terminal's `PATH`. If its build cannot find the SDK, set a user-defined Xcode Build Setting `PRAXIS_DOTNET_EXECUTABLE` to the absolute executable path (or install .NET in a standard location). Separately, the app's first-run setup can select the .NET executable used to launch the worker. A locally configured path is not committed to the project. Select Xcode once using `sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer` or set `DEVELOPER_DIR` for that terminal.

`scripts/macos/build-desktop.sh` now uses **the exact same Xcode application project and shared scheme**, not a parallel Swift Package build or custom handwritten `Info.plist`. It packages an unsigned local `.app` only if `.artifacts/Praxis.app` is absent. `scripts/macos/test-worker-session.sh` runs `xcodebuild test` against the hosted native test target and includes the published worker integration fixture. Direct Xcode testing also discovers the bundled worker when a supported runtime is installed.

The packaging script refuses to overwrite an existing app bundle. To rebuild, remove only the previous development bundle under `.artifacts/`—never the private data directory. The resulting app is unsigned and intended for local development, not distribution.

The collections-preview development app uses a separate macOS preferences identifier from the previous Praxis prototype, so it cannot inherit the earlier app's stored catalog path by accident. This is an isolated testing identity, not a production signing or update decision.

First-run path selection is explicit. Choose a parent folder for a new private `PraxisData` child, and a separate existing directory to watch. They cannot overlap. The catalog must be private to the OS owner. Praxis never creates a replacement if a previously configured desktop catalog disappears: reconnect or restore the real one. Paths, but not source contents or agent secrets, are kept in owner app settings. Pick non-cloud-synchronized folders if you do not want iCloud or third-party sync software to replicate the files independently of Praxis.

After launching, drop Markdown, UTF-8 text, and an unsupported binary into the selected Inbox without issuing an import command. Verify that the Library shows separate source identities, version, byte length, SHA-256 and supported/unsupported extraction outcomes. Search a known term; inspect a source-backed preview; verify the stored original. Modify the text and check revision behavior after reconciliation. Exit and relaunch to check durable identity and search.

A watched root that disconnects after launch is handled by the worker's existing offline/retry logic; source originals are retained. Real unplug/reconnect identity, overflow and intermittent-write testing remain open (#105/#130).

## Offline catalog recovery

Quit Praxis before backing up its data directory. From a Release build, run the following with your actual absolute paths. Choose existing owner-private parent directories and fresh names for the backup and restored catalog. The example limit is 10 GiB.

```sh
dotnet run --project src/Praxis.Worker -c Release --no-build -- \
  recovery backup /absolute/PraxisData /absolute/private-backups/backup-01 --max-bytes 10737418240
dotnet run --project src/Praxis.Worker -c Release --no-build -- \
  recovery verify /absolute/private-backups/backup-01 --max-bytes 10737418240
dotnet run --project src/Praxis.Worker -c Release --no-build -- \
  recovery restore /absolute/private-backups/backup-01 /absolute/private-restore/PraxisData --max-bytes 10737418240
```

Backup opens the existing database read-only and holds the normal catalog lock. It snapshots schemas v2 through the current version without migrating the source. Restore upgrades the staged copy where needed. Successful commands return the original object count, snapshot bytes and snapshot catalog SHA-256 as JSON. The new directory becomes visible after verification.

Keep snapshots private because they contain plaintext originals and catalog data. The byte limit covers the snapshot database and its recorded originals. A failed or interrupted operation can leave a private `.incomplete-...` directory. Use the independently verified backup for a retry. Native recovery controls and removable-media acceptance remain open. See [ADR 0018](../decisions/0018-coherent-local-backup.md) and the [physical recovery checklist](../testing/physical-catalog-recovery.md).

## Protocol boundaries

A request is newline-delimited JSON containing a `requestId`, an allowlisted `action`, and bounded action-specific fields. The initial actions were `library`, `search`, `preview`, `verify`, `retry`, `revoke` and `stop`; merged work has added bounded collection, note, export, model, entity and verifier operations through the same allowlisted private protocol. The desktop handler limits request JSON to 16 KiB, pages to 200 records, search results to 30 and preview content to 12,000 characters. Underlying search and catalog APIs retain their own input validation and source-revocation checks.

The bridge is **not** an external integration protocol. Do not expose it through an unauthenticated socket or HTTP listener; independently authorized external callers still require the controls in ADR 0021 and #107/#117. It does not accept arbitrary paths, file execution or arbitrary SQL as desktop actions.

## Remaining First-use / Second Brain gates

- Confirm native Xcode build, worker publication, first-run window, app-hosted Swift tests and an owner-safe mixed-source capture/search/restart session (#215/#130). Merged #216–#220 are implementation, not a passing hardware run.
- Confirm original export/open and denial paths on physical/symlinked destinations (#166), collection edits and persistence (#146), quick capture (#113), watcher rename/overflow/disconnection (#105), and full local backup/restore (#128).
- Evaluate derived suggestions, entity linking and human/verifier corrections against representative false-positive, stale-source and access-denial cases (#115/#116). Ollama and approved CLI verification are implemented (#205–#212); full grounded assistant/chat and authorized tools are not (#33/#32/#143).
- Add explicit URL/article and browser capture (#104/#111/#112), supported text/PDF/media depth (#103/#114), and independently verify timecoded playable video and editable financial artifacts through the assistant (#177/#178/#170).
- Restore executing cross-platform CI when the owner approves runner provisioning (#137); keep release signing, notarization, runtime distribution and security gates separate.

#101 is already closed for the initial capture milestone. Keep #105/#108/#130 and their specific incomplete acceptance issues open. A first-party application with merged UI code is not a released or fully verified intelligent Second Brain.

## P1/L0 explicit collections checkpoint (#146)

A schema-v6 migration adds owner-authored named collections and many-to-many source membership to the same private SQLite catalog. Collections do **not** reorganize watched files, mutate immutable originals, or assert inferred facts. The merged workbench (#147) integrates create/rename/archive controls, source membership editing, collection-filtered library browsing, and active source counts through the existing private child-process protocol. Existing source IDs and manual collection assignments persist when files are revised; revocation and archive suppress collection-filtered retrieval without erasing historical records or shared originals.

Collection names must be one to eighty printable characters and are uniquely normalized for stable local comparison. Operations use parameterized SQL, explicit source/collection IDs, bounded read pages, and active-source checks; no arbitrary SQL or direct file-path mutation becomes an application action. The new migration requires a compatible updated app: do not open a schema-v6 catalog with older Praxis builds. Do **not** select your valuable existing catalog for the unverified preview. Use a new disposable private catalog and watched Inbox. Before any deliberate migration of real data, create and verify a coherent catalog-plus-originals backup with the existing snapshot operation; older Praxis binaries refuse schema v6.

Before calling this feature verified, execute the full .NET suite, local SQLite upgrade/downgrade-rejection and backup/restore tests, macOS Swift compilation, and a real UI flow: create a collection; assign two independently captured sources; update one source; restart; rename the collection; remove a membership; archive it; verify old original digests; and confirm revoked sources do not appear in collection-filtered views. A queued CI run is not passing evidence.

This is the deterministic first-use organization layer, not autonomous knowledge enrichment. Merged #115/#116 implementations derive separately reviewable suggestions and typed relationships with source, digest, revision and processor provenance; broader dynamic-collection and quality acceptance remain open. External agent retrieval or mutation still requires separately approved, source-scoped grants (#117–#119), and no model invocation is triggered by adding a file or collection.

 
## Quick note creation and deliberate paste (#113)

The first-party Quick note section accepts a title and plain-text/Markdown body. It does not read the system clipboard until the owner explicitly pastes into the editor, and never automatically fetches a pasted URL. Command-Return submits a bounded request (8,000 UTF-16 characters max, 32 KiB original bytes). The title, if present, becomes a Markdown heading; source bytes are stored verbatim as the explicitly authored composed note.

Praxis writes a unique, owner-private Markdown file in the selected watched Inbox using same-directory staging, durable flush and atomic publication; it then commits those exact submitted bytes using the existing content-addressed catalog capture method and processing queue. A stable capture UUID gives in-process retries the same source and receipt, with no overwrite on changed content or title. An interrupted publication may leave a staged `.part` file ignored by the watcher; a committed note file is recovered by normal restart reconciliation. No arbitrary application-chosen path or external importer is introduced.

The editor keeps the unsaved draft in process memory after a failed save until the user discards or retries it; **drafts are not yet crash-durable**. The UI clears the draft only after a confirmed worker acknowledgement. Captured files and accepted source records persist independently of the editor's transient state. The first-use test must exercise duplicate click/retry, file/callback interruption, offline root, changed body using same ID, invalid input, and restart indexing. This note workflow does not make the full browser bookmark/article acceptance #104 complete.


## Historical heading proposal foundation (schema v7, #115)

After an eligible Markdown source has been preserved, fully extracted and indexed, the existing local watched worker runs a bounded deterministic proposal pass. It identifies **only literal ATX headings of levels 1–3** from the revisioned extracted text, excluding ordinary fenced blocks, with a maximum of eight candidates per source revision. Plain text, unsupported formats and sources with no supported heading are not silently assigned topics; eligible heading-free Markdown records an explicit `abstained` run. No language-model request, remote content transmission or agent tool permission is initiated.

Each pending candidate carries the original source ID, item ID, revision, original SHA-256, deterministic producer version, exact heading text and UTF-16 character offset in the extracted content. A candidate is a reviewable **proposal**, not an accepted fact or user-authored collection membership. In Library source details, the owner can inspect the evidence and accept, reject or replace a candidate. Review decisions and their history live in the same private SQLite catalog and coherent backup as source collections. A correction remains marked as an owner correction; it does not rewrite the original Markdown.

A source revision update invalidates active candidate retrieval and review of the previous revision immediately, while preserving historical review records. Source revocation has the same access fence. Proposal generation revalidates source status and latest revision under the catalog transaction so a stale background read cannot publish into the active knowledge view. Startup reconciliation will backfill eligible already-extracted Markdown sources as well as newly imported files; the bounded budget processes at most 16 source revisions per sweep.

**This original schema-v7 slice alone did not deliver** model-backed inference or cross-source relationships. Later merged #205–#212 add bounded Ollama semantic proposals, linked entities/relations and owner-approved CLI verification; summaries, evaluation and automatic collection assignment remain outside this slice. Heading extraction is intentionally literal and may abstain on an ordinary text document with no Markdown structure. Those larger #115/#116/#149/#154 goals require separately versioned processors, evaluation cases, explicit reviewed output and permitted local-model routing. Optional external inference must never be selected automatically after a local failure.

The enrichment preview uses a new macOS bundle preferences identifier (`dev.praxis.enrichment-preview`) rather than inheriting the earlier collections preview's saved catalog paths. This avoids automatically opening and upgrading the prior preview's stored catalog on first launch. The preference change is a development safeguard, not production signing or a release migration policy.

A schema-v7 catalog cannot be opened by older Praxis binaries. Before intentionally upgrading a valuable store, create and independently verify a coherent catalog-plus-originals backup. For this unverified preview, choose a fresh disposable catalog and Inbox. The owner has deferred native macOS verification until a more complete usable workflow; neither a merged branch nor queued CI is acceptance evidence.


## Explicit original export and recovery — #166

The Library's **Export original…** action opens the macOS Save dialog. Only the owner-selected destination is sent through the existing trusted parent/child-process pipe. The action is not exposed through private HTTP or an agent integration. The worker resolves the source's canonical item, active status and current revision, then streams the preserved content-addressed object in fixed-size chunks to a new owner-private staging file in the chosen destination directory. It verifies the full recorded byte length and SHA-256 before flushing and moving the staged file into place. The destination cannot overwrite any existing file, nor can it resolve inside the private Praxis catalog through linked ancestor directories.

The workbench refuses destinations inside its approved watched Inbox to avoid immediately re-capturing exported duplicates, and it suspends automatic UI polling while a large export is in progress. The **watched original path is not used** for export; missing/offline/renamed intake files do not prevent exporting a still-active preserved source. Unsupported binary originals are exportable without needing a text processor. Exporting does not assign topics or grant an agent access. Revocation or a newer source revision invalidates the selected version for export, including a second active-source fence immediately before publication. Failed, canceled or corrupted exports leave the canonical original unchanged and remove only their new staging file.

An export is an owner-authorized copy, not a backup of the entire knowledge base. Saved files may be picked up by iCloud or other sync software if the chosen destination belongs to such a service. The app does not delete or mutate those files afterward. Existing user files must be exported under a different destination name; a Save-panel replacement confirmation cannot override the worker's no-overwrite policy.

### Combined owner MVP smoke, deferred until the first-use app is ready

Choose a **fresh disposable catalog** and separate approved Inbox for first-run testing. Capture Markdown and an unsupported binary; save a quick note; inspect and search; assign collections; verify an original SHA-256; export the binary to an empty destination and compare its bytes; remove the watched source path and export again; attempt a conflicting target; revoke a source and verify denial; restart and repeat retrieval. Rehearse a catalog-plus-originals verified backup/restore separately under #128/#130. Run the full .NET and SwiftUI build and formatting gates before a release/merge-readiness decision. A queued job without runners is not a passing check.


## Watched Inbox health, pause and recovery — #105

The Configuration page now shows an explicit private worker health snapshot instead of treating a live process as proof that intake is working. The states are **starting**, **watching**, **degraded**, **offline**, **paused**, **resuming**, and **stopped**. The snapshot includes the last successful folder scan, observed file count, newly captured revisions since this worker started, total per-file errors, up to eight affected paths and reasons, and an actionable service-level failure reason. This information is returned only over the existing inherited-pipe first-party protocol; no network endpoint or new storage authority is added.

- **Offline:** the approved watched folder is unavailable. On subsequent launches the existing private catalog and Library can still open without that Inbox; first-time setup continues to require an available selected folder. Praxis retains previously captured originals and continues already-queued text processing. Reconnect the existing path; the worker detects it and reconciles missed changes. Praxis never recreates a missing approved directory or silently substitutes a different intake root.
- **Degraded:** a scan, watcher setup, or individual source failed. The original is not discarded or rewritten. Inspect the error, correct file access, file stability or watcher permissions, and choose **Retry scan**; the background periodic sweep also continues when not paused. If notifications are unavailable, the worker reports periodic polling rather than pretending the event watcher is healthy.
- **Pause intake:** stops new filesystem scanning on the next scheduler turn but does not cancel a scan/import already in flight; the processing pipeline runs independently and keeps draining queued jobs while intake is paused or offline. **Resume intake** wakes the watcher and reconciles changed sources. **Retry scan** wakes the existing loop; it is not a destructive rebuild, reset, reauthorization or guaranteed rehash of unchanged verified content.

File errors are deduplicated per path and cleared when the file successfully reconciles or disappears from the observed directory. The UI reads a bounded immutable snapshot; slow or large-file hashing is not performed on the UI thread. These diagnostics are **session-local**, not a permanent historical audit trail: restarting the worker resets the counters and transient error list. Durable source revisions, original digests, processing jobs and owner review decisions remain in the catalog.

The larger #105 acceptance remains open: canonical rename attribution, multi-root supervision, disconnected/replaced-volume identity, queue backpressure and real removable-volume fault injection have not been verified or implemented by this slice. An existing path reappearing does **not** itself prove the same underlying device has been reconnected. Keep #130 open until actual macOS start/stop/offline/resume, disk-full and denied-access behavior has been tested alongside original recovery.


## Preserving identity across observed file renames — #105

The approved Inbox watcher now records a **file rename notification** as a bounded, short-lived candidate for source continuity. During the next scan it checks that both paths stay under the approved root, the previous path no longer exists, the destination is a regular stable file without a link, its complete SHA-256 matches the current preserved source revision, the original source is active, and no source already owns the destination. Only then does Praxis change the source's locator in a single catalog transaction. It preserves source ID, item ID, revision, original bytes, collection assignments, searchable derivatives, accepted topic reviews, and existing receipts. Each verified move receives a durable `source_locator_moves` entry with its old/new paths, original digest and UTC timestamp. In the Library inspector, **Observed file moves** shows recent path evidence for active sources.

A rename without an observed event, one missed during shutdown/overflow/offline operation, a rename involving a modified target, a directory-tree move, or an ambiguous occupied path **does not merge by hash or filename resemblance**. Such a path may enter as a separate new source on ordinary intake. Same-content copies always remain independent. A revoked source identity is never transferred to a new path; physically appearing new paths are treated under the existing independent-source intake policy, so revocation is not a filesystem-level prohibition on reimporting the same bytes under a different name.

Rename candidates are bounded to 128 events, retained for at most one minute, and discarded after a watcher recreation/approved Inbox disconnect. Hash inspection stays within the existing 2 GiB per-scan budget and does not borrow agent privileges, introduce full-tree inference, or traverse linked files. Excess or rejected notifications cause the ordinary non-merging capture path to be used. A confirmed move is **not proof of persistent device identity**. Unplug/reconnect and a different volume mounted at the same path still require stronger approval and physical tests under #105/#128/#130.

Schema v8 adds only the source-locator move journal. Existing v1–v7 stores and versioned snapshot manifests remain supported through migration, but older Praxis builds cannot open v8 after upgrade. Use a fresh disposable catalog when testing this draft; for valuable data, take and independently verify a coherent original-plus-catalog backup first. Moving a file does not transfer it outside the selected Inbox, delete anything, or change your manual organization.

Owner acceptance should demonstrate a file moved after a successful capture, its original ID and collection remaining stable, a same-content copy producing a different source, reuse of the previous filename without idempotency errors, and a missed/rejected rename remaining independent. Then restart, search, export, verify backup/restore and run a real macOS FileSystemWatcher rename. The deterministic unit tests inject the same bounded event record; **actual OS notification delivery is unverified**.

## Source-linked caption search

Search displays timecoded matches from captured `.vtt` and `.srt` sidecars. A cue links to a video only when the sidecar filename names the full SHA-256 digest of that active video original. Same-stem captions without a digest remain searchable text. Integrated playback and first-party chat are tracked under #177 and #33. See [ADR 0027](../decisions/0027-cross-domain-evidence.md) for the evidence contract.

## Media inspection (read-only development capability)

Select an active captured `.mp4`, `.mov`, `.m4v`, `.webm` or `.mkv` in **Inbox & Library**, then choose **Inspect verified media header**. The trusted worker reads and verifies the immutable original object rather than opening the watched Inbox path. The UI identifies the exact source/item/revision/digest, recognized ISO BMFF header or supported EBML document-type signature, source size and optional bounded movie-header duration. An unrecognized header is presented explicitly as unsupported.

For this initial implementation, inspections are owner-initiated and bounded to 512 MiB of original bytes, 64 KiB retained in memory and a 30-second deadline. Missing, revoked, changed or corrupted originals do not produce valid results. A duration is unavailable unless an early `moov/mvhd` header is present and sane. Signature recognition does not establish video playback, track correctness, caption alignment, codec support, visual analysis or a searchable tutorial answer. Owner macOS validation and #114/#177 acceptance remain open.

## Knowledge, verification and model settings

Three workspaces expose the enrichment and verification layers (ADRs 0031–0034):

- **Knowledge** lists entities across all sources with type, where the type came from (local model, verified, set by you), mention and source counts, and type conflicts. An entity shows its verbatim mentions with source names, relations with their evidence, entities mentioned alongside it, and aliases. Owner actions: set type, rename, merge, mark as not a real entity (and restore), add relations, and ask a stronger model to settle its type.
- **Verification** lists requests planned from flagged evidence and type conflicts, or created by the owner from a source's suggestions or an entity. A request awaiting approval shows each item with its quotes; unchecked items are removed and redacted phrases are replaced everywhere, including other items' quotes, and the panel shows exactly what approval will send. After checking, verdicts, notes and token usage are shown alongside what was sent.
- **Models** selects the local Ollama model (listing installed models) and configures Codex and Claude Code as verifiers: CLI path (suggested from PATH, stored as the stable link, not a versioned target), optional model, cost weight and daily limit. Re-saving a provider retries requests that were waiting on it.

Source suggestions now include the local model's entities and relations, mark instruction-like evidence, and offer a stronger-model check for the source's pending items.

## Production packaging and security boundaries

Xcode owns the **native app's lifecycle, Info.plist, code signing, debugger, test host and build configuration**. The separate .NET project continues to own the worker protocol, catalog and inference workloads. The app/worker boundary remains the existing inherited private stdin/stdout pipes; no new local HTTP endpoint or broad agent privileges are introduced.

The development app uses an **ad-hoc local signature in Xcode** (the CLI staging script deliberately produces an unsigned development copy), is **not Mac App Store sandboxed**, and still requires an installed .NET runtime. These are explicit limitations: shipping to users will require an owner-approved signing Team ID, hardened-runtime and notarization assessment, supported architecture matrix, a self-contained/signed or otherwise managed worker runtime, security-scoped access for sandboxing if pursued, app update/rollback rules and actual installation tests. Do not label this migration as a production-ready distribution package or enable the App Sandbox while its owner-selected directories and child-process boundary are unadapted.

**Review/verification gates:** `xcodebuild -list -project apps/macos/Praxis.xcodeproj`; ⌘R creates a visible Praxis window with a real bundle identifier; the app contains `Contents/Resources/worker/Praxis.Worker.dll`; fresh disposable Inbox capture/restart/search works; ⌘U runs Swift tests without opening an owner's catalog; packaging and worker integration scripts succeed; .NET locked restore/build/tests and CI pass on eligible runners. This branch is not yet verified by an owner-run Xcode build.
