# Platform verification strategy

Praxis targets macOS, Windows, Linux, Android, iOS/iPadOS and browsers. Support must be demonstrated; a shared codebase alone is not evidence of platform parity.

## First-party platform acceptance matrix

The first useful product is a **local owner-only Praxis Inbox/Library/Search experience**, delivered independently of Obsidian. Subsequent platforms are targets, not promised support inferred from shared code or a CI build.

| Environment | Intended Praxis outcome | Evidence required before claiming support |
| --- | --- | --- |
| macOS | First standalone local Praxis release (#108/#130) | Actual GUI first-run with chosen data directory, mixed file/folder intake, restart/retry, source-grounded search, original recovery, permission denial and keyboard/accessibility smoke |
| Windows | First-party local client after verified platform packaging | Real package/launch, case-insensitive and reserved-path behavior, permissions, file locking, crash/restart and source recovery |
| Linux | First-party local client after verified platform packaging | Actual launch, filesystem permissions, symlink/path behavior, file watcher limits if enabled, restart and recovery |
| Browser | Local first-party interface, followed by secure independently accessed client | Explicit caller/origin policy (#129/#107), denied cross-origin/rebinding attempts, accessible Inbox/Search, reload and offline-state behavior |
| Android | Later first-party client (#39) | Real-device sandbox/share flow, permission and lifecycle tests, offline capture, suspend/resume and recovery |
| iOS/iPadOS | Later first-party client (#39) | Real-device sandbox/share flow, foreground recovery, offline persistence, termination and secure credential handling |

Optional Obsidian desktop/mobile interoperability remains an **independent M3 adapter gate** (#22/#23/#25), not a prerequisite for selecting or verifying first-party Praxis. Real Obsidian event, vault and lifecycle tests must be recorded separately and must not substitute for #130.

Actual supported OS versions, CPU architectures and device inventory must be recorded alongside each first-party or adapter acceptance.

## Automated build verification

The CI pipeline checks .NET 10.0.403, locked NuGet restore, Release compilation, .NET tests, and API publication on Ubuntu 24.04, macOS 15 (GitHub-hosted Apple Silicon), and Windows Server 2025. Linux also verifies a published API's loopback health endpoint, the lockfile/license inventory, formatting and roadmap validation.

This CI matrix validates **build and test portability for the current backend**, not the first-party local GUI acceptance (#130), real-device Obsidian editor parity or supported client platforms. The Mac runner's Apple Silicon architecture and actual CI outcomes must be confirmed by the associated successful GitHub Actions run; passing Linux checks alone does not establish cross-platform evidence. macOS/Windows hosted CI jobs on a private repository consume GitHub Actions usage, so the matrix should be revisited if usage budgets require it.

## Obsidian adapter constraints

- Use the public Obsidian vault and platform APIs as applicable; don't assume Node.js or Electron APIs on mobile.
- Design change ingestion to tolerate repeated, coalesced, missing, and out-of-order filesystem notifications; reconcile from persistent state.
- Never require mobile work to execute continuously after an app is backgrounded or terminated.
- Prefer safe foreground synchronization on application startup/resume. Background opportunities are optimizations, not correctness assumptions.
- Plugin disable/uninstall must not delete the user's canonical data. User-visible sync status must distinguish local changes from server acknowledgment.

References: [Obsidian mobile plugin development](https://docs.obsidian.md/Plugins/Getting%20started/Mobile%20development), [Apple background execution](https://developer.apple.com/documentation/Xcode/configuring-background-execution-modes), [Android background restrictions](https://developer.android.com/develop/background-work/background-tasks/bg-work-restrictions).

## Cross-device test scenarios

| Scenario | Expected property |
| --- | --- |
| Edit while both clients are offline, then reconnect in either order | Both contributions preserved or explicit conflict with recoverable originals |
| Concurrent edit and deletion | No silent overwrite or unexpected resurrection |
| Rename and edit in different clients | Stable identity retained; collision resolved explicitly |
| Send same change more than once | No duplicate effect; acknowledgment is stable |
| Interrupt transfer or terminate process before commit | Resume/retry safely without partial canonical content |
| Encounter an old client after format evolution | Reject or upgrade explicitly; preserve older data |
| Run out of disk space or fail an integrity check | No partial successful acknowledgment; clear recovery path |
| Restart after incomplete pull or push | Last durable cursor and local edits remain consistent |
| Revoke a device with pending changes | Access denied; remaining devices unaffected; offline content handling documented |
| Mobile app is suspended for hours or days | User can open it, reconcile, and observe pending changes |

## Evidence gates

- **Contract tests:** platform-agnostic state machine and protocol fixtures reproduce conflicts, retries, and recovery.
- **Adapter tests:** platform-specific local mutation and lifecycle behavior verified against the canonical contract.
- **Real-device tests:** minimum one confirmed device per supported mobile operating system before advertising mobile parity.
- **Release verification:** repeat tests with upgrades, corrupted attachments, expired credentials, and degraded connectivity.

No continuous background synchronization guarantee is made for iOS or Android.
