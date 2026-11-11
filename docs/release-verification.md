# SDK and release verification

Praxis uses the exact .NET SDK `10.0.403` with `global.json` roll-forward disabled. Install that SDK before running the solution locally; newer SDKs installed alongside it will **not** be substituted automatically. CI installs the version selected in `global.json` using the pinned setup-dotnet action.

The required pull-request pipeline runs locked NuGet restore, Release build and .NET tests on Ubuntu 24.04, macOS 15 (Apple Silicon runner) and Windows Server 2025. The Linux job also checks formatting, planning validation, dependency inventory and the published API's HTTP liveness after launching its output as a separate local process.

These checks establish a consistent SDK, dependency graph and baseline portability for the current source. They do **not** establish byte-identical published binaries across operating systems, guarantee offline restore with no package cache, exercise client filesystem adapters on real devices, or validate database migrations/rollbacks before a schema exists.

Before approving a release, compare the new [NuGet dependency/license inventory](../dependencies/README.md), resolve advisory and license exceptions, verify that supported operating systems pass CI, and confirm separate migration, recovery, privacy and deployment gates. Additional platform coverage and host-specific packaging require evidence tied to supported release targets.

To upgrade the SDK intentionally, update `global.json` in a reviewed PR, install the candidate in CI on each target OS, regenerate any affected NuGet locks and inventory, then re-run the full matrix. Do not relax SDK roll-forward or turn off locked restore as an upgrade workaround.

## Native macOS development acceptance (#215)

The checked-in `apps/macos/Praxis.xcodeproj` and shared `Praxis` scheme are the single source of truth for SwiftUI debugging, hosted Swift Testing, packaging and macOS CI. `scripts/macos/publish-worker.sh` publishes the canonical .NET worker inside the app bundle; a matching local .NET 10 runtime remains required. The app is an **owner-local unsigned preview**, not a signed or notarized external distribution.

From a macOS machine with full Xcode and the pinned .NET SDK:

```bash
git rev-parse HEAD
git status --short
bash scripts/macos/build-desktop.sh --archive-existing
bash scripts/macos/test-worker-session.sh
```

Quit Praxis first. The explicit `--archive-existing` option preserves any prior `.artifacts/Praxis.app` in `.artifacts/previous-builds` (and refuses to move it while Praxis is running); the default command still refuses to overwrite it. The package script checks `CFBundleIdentifier`, `CFBundleExecutable`, `LSMinimumSystemVersion=13.0` and the bundled `Praxis.Worker.dll`. The hosted tests exercise the worker's capture/search/restart transport and reject malformed responses; a missing worker or .NET executable **fails** the transport suite rather than silently skipping it. `PRAXIS_DOTNET_EXECUTABLE` can name an SDK outside the interactive shell `PATH`. Record the produced `.xcresult` and any worker publish errors from Xcode's expanded build log.

For **interactive** verification, quit any running Praxis instance, protect existing preview preferences (or use a disposable macOS user), and select the same `Praxis` scheme in Xcode. Run **⌘B, ⌘R and ⌘U**. Observe the actual app window and successful child-worker startup, then approve a **fresh disposable** catalog parent and watched Inbox; capture a text file, search it, restart, and verify original identity without creating duplicate receipts. Never open valuable owner data from a test. The preview uses the stable `dev.praxis.renames-preview` identifier and therefore may resume a previously configured catalog; preparing test-owned settings before ⌘R is required.

Before external release, #36/#130 still require a documented signing, hardened runtime, notarization, bundle architecture and .NET runtime distribution decision, update/rollback and owner-approved filesystem permission policy. The three-OS Actions baseline is separately blocked under #137; a queued or cancelled run cannot certify the native or cross-platform gate.
