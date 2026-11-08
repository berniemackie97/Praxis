# ADR 0014: Self-contained desktop plugin package and lifecycle cancellation

**Status:** Proposed for #22 and #57.

## Decision

The desktop adapter now has an explicit packaging boundary. Obsidian loads a CommonJS `main.js`, but the original source is intentionally divided into the UI entry, archive session and bounded CLI process modules. The repository therefore provides a **fixed-input, deterministic, offline packager** at `integrations/obsidian/scripts/package.js`. It embeds the three known modules in a single `main.js` and copies the desktop-only `manifest.json`. Builtin Node.js modules and Obsidian itself remain external; all other dependencies are rejected by the package loader.

This tool is specific to the three source files. It does not attempt to implement general Node module resolution, dynamic imports, source maps or a universal bundler. It requires no npm installation or runtime downloads. The package is staged under a random sibling pending directory before publication; an existing destination is not overwritten, and the plugin source directory cannot be selected as the destination. The two generated files constitute the manual installation payload.

## Session and unload boundary

A changed archive-directory or CLI setting increments the plugin's session generation and closes the old session. Asynchronously opening sessions verify the generation before becoming active and reject stale resolution. Plugin unload increments that generation and closes the active session. Archive session close marks the adapter closed, invalidates the pending vault-generation check and aborts its running CLI process. Capture and verification must not adopt a new archive pointer after the session has been closed. The session-state writer also checks closed status before publishing its replacement.

The UI does not display a capture or preview result after unload. A failed command or an interrupted capture leaves the last successful archive pointer intact. Already-written, unadopted archive files remain in the chosen external directory for operator inspection; the adapter does not delete or promote them automatically. These are process-local guarantees, **not** a transactional snapshot of concurrent external vault writers.

## Verification

CI builds the two-file package on Linux, macOS and Windows, syntax-checks the generated entry and loads it against a simulated Obsidian host. Tests verify command registration, deferred vault listener attachment, file and folder rename handling, unload/close behavior, deterministic package generation, and no-overwrite boundaries. The archive session tests additionally exercise close during capture and during verification, with unchanged accepted baseline. Existing process-level .NET CLI integration coverage remains in place.

**Acceptance limits:** The simulated host does not establish actual compatibility with installed Obsidian desktop versions, actual event ordering, file-manager folder moves, extension packaging policy, recovery from process crashes, or complete content fidelity. Those need a real-application smoke matrix under #22/#25. The package is desktop-only; it has no mobile support, no network synchronization and no encrypted backup. Owner decisions in #50 remain outstanding.
