# ADR 0013: Manual Obsidian desktop archive adapter

**Status:** Proposed under #22 / #57. This describes a first executable desktop integration, not a verified desktop or mobile release.

## Decision

Use a small Obsidian desktop plugin to invoke the already-validated local `Praxis.VaultCli.dll` with explicit, user-triggered commands. This makes real vault capture and incremental reconciliation accessible from Obsidian without defining a new network protocol or allowing an editor plugin to own canonical records.

The adapter implements two commands: **Capture or update local archive** and **Preview local archive update**. The first command performs a tracked initial capture or baseline-based update, verifies the result and only then advances its local current-archive pointer. The second command runs the nonwriting plan operation, displaying its findings without publishing data. No activity monitor automatically starts an archive operation or changes note content.

The plugin requires Obsidian desktop's filesystem adapter and invokes `dotnet` with an argument array and `shell: false`; subprocess output, runtime and concurrent operations are bounded. There are no network requests or added JavaScript packages. `manifest.json` explicitly marks the plugin desktop-only.

## Durable state and ownership

A caller-chosen, existing archive directory must be **outside** the actual vault path. The plugin stores the current generated snapshot filename and pending one-to-one rename mappings in a versioned `.praxis-vault-session.json` in that directory, along with an exclusive per-operation `.praxis-vault-session.lock`. It neither stores attachment bytes nor moves or deletes the user's original files.

Initial capture uses `--track-links`; subsequent updates preserve stable archive record IDs, history and tracked references. A new archive is generated using a unique name, verified, and then adopted by replacing external session state through a flushed temporary file. Previous snapshots are retained as recovery points. A crash or failure before state adoption can leave an unreferenced newly created archive, which must be reviewed rather than silently adopted or deleted.

File and folder rename events update an external, restart-safe mapping ledger. Folder events are expanded against known prior archive file paths by the CLI, not guessed from matching content. Chained renames and file swaps preserve origin paths, allowing the existing CLI's explicit rename machinery to reconcile stable identities. Creation/modification/deletion events increment a generation marker so an in-flight operation triggered by Obsidian activity refuses to advance its pointer. Events are registered when the Obsidian workspace layout is ready and are automatically unregistered on unload.

## Fail-closed boundaries

- A malformed or missing session baseline, unavailable CLI runtime, conflicting session lock, unsupported rename, invalid source path, failed verification or cancelled operation must not replace the last known-good archive pointer.
- A folder move that Obsidian reports can be recorded as an explicit mapping, but missed events, conflicting file and folder moves, edits while the plugin was disabled, or filesystem changes not surfaced through Obsidian events may require explicit CLI correction. Event generation is **not** a transactional vault snapshot and cannot detect all external file writers.
- Disappearing tracked links require exact identity-specific approval in the CLI per ADR 0012; this initial plugin **does not automate that approval**.
- The adapter cannot remove a stale lock after a crash without operator review. A stale lock blocks progress until the operator verifies no process is running.
- Archives, configuration and ledger contain locally sensitive data and are **not encrypted** by this adapter. The owner controls the target directory, its permissions, lifecycle and backups. This slice does not authorize server access or inference.

## Verification and open gates

Node standard-library tests simulate archive execution, restarts, chained rename events, data corruption, path rejection, concurrent changes, aborted operations and lock conflicts. A separate Linux integration test runs the real .NET CLI through the adapter session across capture, rename, verified update, restore and refused unapproved link removal. CI runs syntax checks and deterministic Node tests on Linux, macOS and Windows in addition to existing .NET verification.

The actual Obsidian application has not yet been smoke tested with the plugin. Desktop event parity, rename behavior during folder moves, file creation semantics, plugin packaging, recovery from interrupted editor sessions, and no-loop proof on a real vault remain under #22/#25. Mobile adaptation is independent under #23; it cannot use these desktop Node APIs. Privacy, hosting and key-custody selections still require #50 before any remotely accessible private data is enabled.
