# Owner-local macOS knowledge workbench

**Status:** Development implementation under #108. The 316-test baseline reported by the owner preceded this slice. The new desktop protocol, SwiftUI application, real watcher behavior and accessibility still require local build/smoke evidence. This is not a signed or released application.

## Trust and lifecycle

The first-party window launches one .NET worker as a private child process. Structured messages travel over its inherited stdin/stdout pipes. Only that worker opens the catalog and owns the existing watched intake, processing leases, verified originals and FTS5 index. No TCP listener, browser route, external inference, MCP grant or agent permission is enabled.

The private pipe supports bounded library pages, local lexical search, versioned extracted-text previews, integrity verification, processing retry and explicit source revocation. The UI asks for confirmation before revocation. Revocation removes a source from active retrieval; it does **not** automatically destroy an original. Original export/open is a separate outstanding UI acceptance criterion.

The first macOS delivery surface does not settle #36's future Windows/Linux/mobile stack decision. The app requires the .NET 10 runtime and Apple's Swift compiler during development.

## Build and first-use smoke

From the repository root:

\`\`\`sh
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
bash scripts/macos/build-desktop.sh
open .artifacts/Praxis.app
\`\`\`

The packaging script refuses to overwrite an existing app bundle. To rebuild, remove only the previous development bundle under \`.artifacts/\`—never the private data directory. The resulting app is unsigned and intended for local development, not distribution.

First-run path selection is explicit. Choose a parent folder for a new private \`PraxisData\` child, and a separate existing directory to watch. They cannot overlap. The catalog must be private to the OS owner. Praxis never creates a replacement if a previously configured desktop catalog disappears: reconnect or restore the real one. Paths, but not source contents or agent secrets, are kept in owner app settings.

After launching, drop Markdown, UTF-8 text, and an unsupported binary into the selected Inbox without issuing an import command. Verify that the Library shows separate source identities, version, byte length, SHA-256 and supported/unsupported extraction outcomes. Search a known term; inspect a source-backed preview; verify the stored original. Modify the text and check revision behavior after reconciliation. Exit and relaunch to check durable identity and search.

A watched root that disconnects after launch is handled by the worker's existing offline/retry logic; source originals are retained. Real unplug/reconnect identity, overflow and intermittent-write testing remain open (#105/#130).

## Protocol boundaries

A request is newline-delimited JSON containing a \`requestId\`, an allowlisted \`action\`, and bounded action-specific fields. The actions are \`library\`, \`search\`, \`preview\`, \`verify\`, \`retry\`, \`revoke\` and \`stop\`. The desktop handler limits request JSON to 16 KiB, pages to 200 records, search results to 30 and preview content to 12,000 characters. Underlying search and catalog APIs retain their own input validation and source-revocation checks.

The bridge is **not** an external integration protocol. Do not expose it through an unauthenticated socket or HTTP listener; independently authorized external callers still require the controls in ADR 0021 and #107/#117. It does not accept arbitrary paths, file execution or arbitrary SQL as desktop actions.

## Remaining First-use / Second Brain gates

- Export/open exact originals through a separately verified safe path and user-selected destination.
- Human-authored categories and dynamic collections, source-grounded relationship editing, and agent-proposed metadata/summaries with explicit accept/reject/abstain semantics (#115/#116).
- Durable observed-presence/progress/error history, safe rename/move attribution, approved multi-root and pause/resume management (#105).
- Explicit URL bookmark capture and approved article snapshots (#104), richer PDF/media processors (#103/#114).
- Local-model/agent orchestration and context packs with scoped read grants, model attribution, source version fences and human approval for changes (#33/#117/#118/#143). **Starting automatic intake never automatically grants an agent access.**
- Actual macOS compile/UI/accessibility/negative-path smoke, three-platform automated verification, physical catalog recovery and release acceptance (#128/#130/#137).

Keep #101, #105, #108 and #130 open until their complete criteria are genuinely verified. The initial application is a usable direction, not a complete or verified Second Brain release.
