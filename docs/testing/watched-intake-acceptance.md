# Watched intake: owner-machine acceptance

**Scope:** Issue #105. Use an expendable private catalog, two ordinary scratch roots and a separate disposable removable or disk-image volume. Never point these tests at the real Praxis catalog, the user's home directory, or valuable external data.

## Prerequisites and evidence

Record `git rev-parse HEAD`, `dotnet --info`, macOS/Xcode versions, exact worker and app bundle identity, the output of the Release build/tests/format/publish gates, and the native app's presented per-root status. A queued GitHub Actions job is not passing evidence. Keep all sensitive path names and captured content out of public logs.

Before real filesystem experiments, create a coherent backup of any existing private catalog. Use a **fresh** test catalog to avoid migration or accidental retention impact.

## Headless and native checks

1. Select a primary folder and one extra folder in the Xcode app. Verify that both appear in Configuration, each has its own state/counts, and an unknown binary is preserved but not indexed. Drop a `.md` and `.txt` source into different roots; confirm automatic capture, preserved source IDs and searchable content. Restart the worker and confirm no duplicate receipt or source.
2. Pause globally, add a source to each root, and confirm neither is imported while paused. Resume and verify both are captured exactly once. Change one file, atomically replace another, rename a directory, and verify revision/source continuity and old-term invalidation.
3. While both roots are running, make one root unavailable (rename a disposable directory). Confirm only that root reports offline while the other continues importing. Restore the same directory; confirm it is not scanned without reapproval. In the app, matching device/inode can trigger automatic approval; otherwise use the folder picker. Verify original integrity throughout.
4. Repeat with a **different** disposable directory or removable volume mounted at the original path. The reattached path must not import before deliberate owner reapproval. Try Quick note while the primary root is unavailable/reapproval-required: no file is created. After reapproval, Quick note should capture normally.
5. In an isolated scratch root, exercise slow writes, same-size/mtime-preserving edits, unreadable files, temporary extensions, a watcher fault or controlled overflow, deletion, and shutdown during processing. A simulated overflow verifies code paths but does not prove kernel notification delivery. Reconcile after restart without missing versions, re-running imports or deleting canonical originals.
6. On an explicitly disposable mounted disk image, test unplug/detach, reattach and a near-full filesystem. Stop before any risk to owner data. Compare the volume identity against the saved approval, original SHA-256s, revision IDs and errors; do not equate a copied directory tree with the same mounted volume.
7. Run Linux and Windows suites when runners are available. Confirm bounded roots/paths, rejected overlapping catalog paths, and no private HTTP interface or unauthorized network traffic. Xcode must build the real app and its hosted test scheme, not only compile Swift packages.

## Closure gate

Do not close #105 until the owner supplies actual macOS folder event, overflow/recovery, volume detach/reattach and disk-full results, native Xcode app verification, and the required cross-platform evidence. Record any residual incidents as explicit new blocking issues; #130/#215 continue to own integrated first-use launch acceptance.
