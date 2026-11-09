# Owner-local macOS knowledge workbench

**Status:** Development implementation under #108. The 316-test baseline reported by the owner preceded this slice. The new desktop protocol, SwiftUI application, real watcher behavior and accessibility still require local build/smoke evidence. This is not a signed or released application.

## Trust and lifecycle

The first-party window launches one .NET worker as a private child process. Structured messages travel over its inherited stdin/stdout pipes. Only that worker opens the catalog and owns the existing watched intake, processing leases, verified originals and FTS5 index. No TCP listener, browser route, external inference, MCP grant or agent permission is enabled.

The private pipe supports bounded library pages, local lexical search, versioned extracted-text previews, integrity verification, processing retry and explicit source revocation. The UI asks for confirmation before revocation. Revocation removes a source from active retrieval; it does **not** automatically destroy an original. Original export/open is a separate outstanding UI acceptance criterion.

The first macOS delivery surface does not settle #36's future Windows/Linux/mobile stack decision. The app requires the .NET 10 runtime and Apple's Swift compiler during development. macOS GUI processes may not inherit a shell's dotnet path; first-run setup can explicitly select a trusted dotnet executable when it is installed outside standard locations.

## Build and first-use smoke

From the repository root:

```sh
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
bash scripts/macos/build-desktop.sh
open .artifacts/Praxis.app
```

The packaging script refuses to overwrite an existing app bundle. To rebuild, remove only the previous development bundle under `.artifacts/`—never the private data directory. The resulting app is unsigned and intended for local development, not distribution.

The collections-preview development app uses a separate macOS preferences identifier from the previous Praxis prototype, so it cannot inherit the earlier app's stored catalog path by accident. This is an isolated testing identity, not a production signing or update decision.

First-run path selection is explicit. Choose a parent folder for a new private `PraxisData` child, and a separate existing directory to watch. They cannot overlap. The catalog must be private to the OS owner. Praxis never creates a replacement if a previously configured desktop catalog disappears: reconnect or restore the real one. Paths, but not source contents or agent secrets, are kept in owner app settings. Pick non-cloud-synchronized folders if you do not want iCloud or third-party sync software to replicate the files independently of Praxis.

After launching, drop Markdown, UTF-8 text, and an unsupported binary into the selected Inbox without issuing an import command. Verify that the Library shows separate source identities, version, byte length, SHA-256 and supported/unsupported extraction outcomes. Search a known term; inspect a source-backed preview; verify the stored original. Modify the text and check revision behavior after reconciliation. Exit and relaunch to check durable identity and search.

A watched root that disconnects after launch is handled by the worker's existing offline/retry logic; source originals are retained. Real unplug/reconnect identity, overflow and intermittent-write testing remain open (#105/#130).

## Protocol boundaries

A request is newline-delimited JSON containing a `requestId`, an allowlisted `action`, and bounded action-specific fields. The actions are `library`, `search`, `preview`, `verify`, `retry`, `revoke` and `stop`. The desktop handler limits request JSON to 16 KiB, pages to 200 records, search results to 30 and preview content to 12,000 characters. Underlying search and catalog APIs retain their own input validation and source-revocation checks.

The bridge is **not** an external integration protocol. Do not expose it through an unauthenticated socket or HTTP listener; independently authorized external callers still require the controls in ADR 0021 and #107/#117. It does not accept arbitrary paths, file execution or arbitrary SQL as desktop actions.

## Remaining First-use / Second Brain gates

- Export/open exact originals through a separately verified safe path and user-selected destination.
- Human-authored categories and dynamic collections, source-grounded relationship editing, and agent-proposed metadata/summaries with explicit accept/reject/abstain semantics (#115/#116).
- Durable observed-presence/progress/error history, safe rename/move attribution, approved multi-root and pause/resume management (#105).
- Explicit URL bookmark capture and approved article snapshots (#104), richer PDF/media processors (#103/#114).
- Local-model/agent orchestration and context packs with scoped read grants, model attribution, source version fences and human approval for changes (#33/#117/#118/#143). **Starting automatic intake never automatically grants an agent access.**
- Actual macOS compile/UI/accessibility/negative-path smoke, three-platform automated verification, physical catalog recovery and release acceptance (#128/#130/#137).

Keep #101, #105, #108 and #130 open until their complete criteria are genuinely verified. The initial application is a usable direction, not a complete or verified Second Brain release.


## P1/L0 explicit collections checkpoint (#146)

A schema-v6 migration adds owner-authored named collections and many-to-many source membership to the same private SQLite catalog. Collections do **not** reorganize watched files, mutate immutable originals, or assert inferred facts. This draft workbench integrates create/rename/archive controls, source membership editing, collection-filtered library browsing, and active source counts through the existing private child-process protocol. Existing source IDs and manual collection assignments persist when files are revised; revocation and archive suppress collection-filtered retrieval without erasing historical records or shared originals.

Collection names must be one to eighty printable characters and are uniquely normalized for stable local comparison. Operations use parameterized SQL, explicit source/collection IDs, bounded read pages, and active-source checks; no arbitrary SQL or direct file-path mutation becomes an application action. The new migration requires a compatible updated app: do not open a schema-v6 catalog with older Praxis builds. Do **not** select your valuable existing catalog for the unverified preview. Use a new disposable private catalog and watched Inbox. Before any deliberate migration of real data, create and verify a coherent catalog-plus-originals backup with the existing snapshot operation; older Praxis binaries refuse schema v6.

Before calling this feature verified, execute the full .NET suite, local SQLite upgrade/downgrade-rejection and backup/restore tests, macOS Swift compilation, and a real UI flow: create a collection; assign two independently captured sources; update one source; restart; rename the collection; remove a membership; archive it; verify old original digests; and confirm revoked sources do not appear in collection-filtered views. A queued CI run is not passing evidence.

This is the deterministic first-use organization layer, not autonomous knowledge enrichment. Future #115 suggestions will be derived, explicitly marked proposed/accepted/rejected/corrected with source, digest, revision and processor provenance. #116 will add reviewable relationships/dynamic collections on top of these owner-owned identities. External agent retrieval or mutation still requires separately approved, source-scoped grants (#117–#119), and no model invocation is triggered by adding a file or collection.

 
## Quick note creation and deliberate paste (#113)

The first-party Quick note section accepts a title and plain-text/Markdown body. It does not read the system clipboard until the owner explicitly pastes into the editor, and never automatically fetches a pasted URL. Command-Return submits a bounded request (8,000 UTF-16 characters max, 32 KiB original bytes). The title, if present, becomes a Markdown heading; source bytes are stored verbatim as the explicitly authored composed note.

Praxis writes a unique, owner-private Markdown file in the selected watched Inbox using same-directory staging, durable flush and atomic publication; it then commits those exact submitted bytes using the existing content-addressed catalog capture method and processing queue. A stable capture UUID gives in-process retries the same source and receipt, with no overwrite on changed content or title. An interrupted publication may leave a staged `.part` file ignored by the watcher; a committed note file is recovered by normal restart reconciliation. No arbitrary application-chosen path or external importer is introduced.

The editor keeps the unsaved draft in process memory after a failed save until the user discards or retries it; **drafts are not yet crash-durable**. The UI clears the draft only after a confirmed worker acknowledgement. Captured files and accepted source records persist independently of the editor's transient state. The first-use test must exercise duplicate click/retry, file/callback interruption, offline root, changed body using same ID, invalid input, and restart indexing. This note workflow does not make the full browser bookmark/article acceptance #104 complete.


## Reviewable topic suggestions (schema v7, #115)

After an eligible Markdown source has been preserved, fully extracted and indexed, the existing local watched worker runs a bounded deterministic proposal pass. It identifies **only literal ATX headings of levels 1–3** from the revisioned extracted text, excluding ordinary fenced blocks, with a maximum of eight candidates per source revision. Plain text, unsupported formats and sources with no supported heading are not silently assigned topics; eligible heading-free Markdown records an explicit `abstained` run. No language-model request, remote content transmission or agent tool permission is initiated.

Each pending candidate carries the original source ID, item ID, revision, original SHA-256, deterministic producer version, exact heading text and UTF-16 character offset in the extracted content. A candidate is a reviewable **proposal**, not an accepted fact or user-authored collection membership. In Library source details, the owner can inspect the evidence and accept, reject or replace a candidate. Review decisions and their history live in the same private SQLite catalog and coherent backup as source collections. A correction remains marked as an owner correction; it does not rewrite the original Markdown.

A source revision update invalidates active candidate retrieval and review of the previous revision immediately, while preserving historical review records. Source revocation has the same access fence. Proposal generation revalidates source status and latest revision under the catalog transaction so a stale background read cannot publish into the active knowledge view. Startup reconciliation will backfill eligible already-extracted Markdown sources as well as newly imported files; the bounded budget processes at most 16 source revisions per sweep.

**This does not deliver** robust topical inference, summary generation, entity extraction, cross-source relationship resolution, a confidence score, a selectable model provider or automatic collection assignment. Heading extraction is intentionally literal and may abstain on an ordinary text document with no Markdown structure. Those larger #115/#116/#149/#154 goals require separately versioned processors, evaluation cases, explicit reviewed output and permitted local-model routing. Optional external inference must never be selected automatically after a local failure.

The enrichment preview uses a new macOS bundle preferences identifier (`dev.praxis.enrichment-preview`) rather than inheriting the earlier collections preview's saved catalog paths. This avoids automatically opening and upgrading the prior preview's stored catalog on first launch. The preference change is a development safeguard, not production signing or a release migration policy.

A schema-v7 catalog cannot be opened by older Praxis binaries. Before intentionally upgrading a valuable store, create and independently verify a coherent catalog-plus-originals backup. For this unverified preview, choose a fresh disposable catalog and Inbox. The owner has deferred native macOS verification until a more complete usable workflow; neither a merged branch nor queued CI is acceptance evidence.


## Explicit original export and recovery — #166

The Library's **Export original…** action opens the macOS Save dialog. Only the owner-selected destination is sent through the existing trusted parent/child-process pipe. The action is not exposed through private HTTP or an agent integration. The worker resolves the source's canonical item, active status and current revision, then streams the preserved content-addressed object in fixed-size chunks to a new owner-private staging file in the chosen destination directory. It verifies the full recorded byte length and SHA-256 before flushing and moving the staged file into place. The destination cannot overwrite any existing file, nor can it resolve inside the private Praxis catalog through linked ancestor directories.

The workbench refuses destinations inside its approved watched Inbox to avoid immediately re-capturing exported duplicates, and it suspends automatic UI polling while a large export is in progress. The **watched original path is not used** for export; missing/offline/renamed intake files do not prevent exporting a still-active preserved source. Unsupported binary originals are exportable without needing a text processor. Exporting does not assign topics or grant an agent access. Revocation or a newer source revision invalidates the selected version for export, including a second active-source fence immediately before publication. Failed, canceled or corrupted exports leave the canonical original unchanged and remove only their new staging file.

An export is an owner-authorized copy, not a backup of the entire knowledge base. Saved files may be picked up by iCloud or other sync software if the chosen destination belongs to such a service. The app does not delete or mutate those files afterward. Existing user files must be exported under a different destination name; a Save-panel replacement confirmation cannot override the worker's no-overwrite policy.

### Combined owner MVP smoke, deferred until the first-use app is ready

Choose a **fresh disposable catalog** and separate approved Inbox for first-run testing. Capture Markdown and an unsupported binary; save a quick note; inspect and search; assign collections; verify an original SHA-256; export the binary to an empty destination and compare its bytes; remove the watched source path and export again; attempt a conflicting target; revoke a source and verify denial; restart and repeat retrieval. Rehearse a catalog-plus-originals verified backup/restore separately under #128/#130. Run the full .NET and SwiftUI build and formatting gates before a release/merge-readiness decision. A queued job without runners is not a passing check.
