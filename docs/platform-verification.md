# Platform verification strategy

Praxis targets macOS, Windows, Linux, Android, iOS/iPadOS and browsers. Support must be demonstrated; a shared codebase alone is not evidence of platform parity.

## Platform matrix

| Environment | Initial client | Required evidence |
| --- | --- | --- |
| macOS | Obsidian desktop | Filesystem event behavior, offline edits, file locking, rename/delete recovery and restart |
| Windows | Obsidian desktop | Case-insensitive paths, reserved names, cross-process locking, rename collisions |
| Linux | Obsidian desktop | Path case, permissions, watching limits, symlinks and atomic replacement |
| Android | Obsidian mobile | Offline/reconnect, app suspend/resume, restricted network, vault lifecycle |
| iOS/iPadOS | Obsidian mobile | Sandbox constraints, suspend/resume, interrupted transfer, foreground recovery |
| Browser | Future client | Offline persistence, secure browser storage, reload/upgrade, accessible conflict resolution |

Actual supported OS versions, CPU architectures and device inventory are to be recorded in test evidence as each platform reaches a release gate.

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
