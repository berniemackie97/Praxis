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
