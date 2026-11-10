# ADR 0036: Xcode-owned native macOS application with a .NET worker

**Status:** Proposed until actual Xcode 26 / macOS owner verification.

## Decision

Use a checked-in `Praxis.xcodeproj` macOS **Application** target with a shared Run/Test scheme as the only first-party macOS application build. Keep SwiftUI, native window state and first-party UI tests under `apps/macos/Sources/Praxis` and `Tests/PraxisTests`; retain all canonical intake, catalog, source processing, inference and knowledge behavior in the `.NET 10` `Praxis.Worker` project.

An Xcode Run Script phase publishes the framework-dependent worker into `Praxis.app/Contents/Resources/worker`. The app uses the existing inherited-pipe transport and the established `WorkerLaunch` resource contract. The debugger launches an actual app bundle with an explicit identifier, unlike the previous `Package.swift` executable Xcode scheme. Swift tests are hosted by the app; test mode disables automatic opening of previously configured owner data.

The command-line local development packager and macOS CI use `xcodebuild` against **that same project**, not a separate hand-built app. The application identifier stays `dev.praxis.renames-preview` for this preview migration, and the minimum system remains macOS 13. No new Swift implementation of backend components is created.

## Security and follow-on work

An Xcode build requires the locally installed .NET 10 SDK. The current worker still requires a locally installed compatible .NET runtime; it is not a self-contained distribution. The development app is unsandboxed to preserve explicitly approved filesystem access and child-process launch. Xcode ad-hoc signing and an unsigned CLI development package **do not** meet production signing, hardened runtime, notarization, architecture packaging or App Store restrictions.

These release requirements remain separate acceptance gates (#36, #108, #130). Adding an App Sandbox entitlement without a proper plan for security-scoped bookmarks and the worker process is not an acceptable shortcut. Test-owner data must never be opened by automated app-hosted unit tests.
