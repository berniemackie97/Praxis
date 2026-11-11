# ADR 0036: Xcode-owned native macOS application with a .NET worker

**Status:** Accepted for owner-local native development; Xcode ⌘R acceptance remains open in #215.

## Decision

Use a checked-in `Praxis.xcodeproj` macOS **Application** target with a shared Run/Test scheme as the only first-party macOS application build. Keep SwiftUI, native window state and first-party UI tests under `apps/macos/Sources/Praxis` and `Tests/PraxisTests`; retain all canonical intake, catalog, source processing, inference and knowledge behavior in the `.NET 10` `Praxis.Worker` project.

An Xcode Run Script phase publishes the framework-dependent worker into `Praxis.app/Contents/Resources/worker`. The app uses the existing inherited-pipe transport and the established `WorkerLaunch` resource contract. The debugger launches an actual app bundle with an explicit identifier, unlike the previous `Package.swift` executable Xcode scheme. Swift tests are hosted by the app; test mode disables automatic opening of previously configured owner data.

The command-line local development packager and macOS CI use `xcodebuild` against **that same project**, not a separate hand-built app. The application identifier stays `dev.praxis.renames-preview` for this preview migration, and the minimum system remains macOS 13. No new Swift implementation of backend components is created.

## Security and follow-on work

An Xcode build requires the locally installed .NET 10 SDK. The current worker still requires a locally installed compatible .NET runtime; it is not a self-contained distribution. The development app is unsandboxed to preserve explicitly approved filesystem access and child-process launch. Xcode ad-hoc signing and an unsigned CLI development package **do not** meet production signing, hardened runtime, notarization, architecture packaging or App Store restrictions.

These release requirements remain separate acceptance gates (#36, #108, #130). Adding an App Sandbox entitlement without a proper plan for security-scoped bookmarks and the worker process is not an acceptable shortcut. Test-owner data must never be opened by automated app-hosted unit tests.

## Verification and distribution boundary

The owner confirmed that the checked-in Xcode scheme builds the actual macOS app and hosts Swift tests against a bundled .NET worker. The unsigned CLI package also launched a real SwiftUI window, and a disposable two-root catalog captured files and recovered through rename, disappearance and reapproval. These observations establish the **development-app** architecture; they do not yet prove Xcode's interactive ⌘R path on the current head, a signed distributable, or a supported deployment on every macOS 13 host.

Pin the app and hosted-test deployment target to 13.0 in both configurations. The development packaging script checks the built bundle's minimum OS, executable, worker and stable identifier. It refuses to overwrite an existing preview bundle; `--archive-existing` moves that bundle into a uniquely named directory under `.artifacts/previous-builds` before packaging its replacement. This is **not** an application updater and never modifies an installed production app.

The developer must use a disposable catalog and approved scratch roots before interactive Xcode Run: the preview bundle identity intentionally persists owner settings. Signing, hardened runtime/notarization, CPU architecture coverage, security-scoped file access if sandboxing, runtime bundling and update/rollback policy are separate product/release decisions under #36 and #130. No HTTP bridge or Swift duplicate of the backend is introduced.
