# Praxis vault adapter for Obsidian desktop

This is the first **local, user-triggered desktop adapter** for Praxis. It uses the existing .NET vault CLI to capture, preview, update and verify a vault archive. It does **not** synchronize to a service, watch files in the background, rewrite notes, delete vault files or create backups automatically.

For a first-use Mac walkthrough with an actual CLI preflight and a new disposable test vault, follow [LOCAL-TRIAL.md](LOCAL-TRIAL.md). The installer accepts an absolute `.NET` executable path to avoid GUI `PATH` differences. For the manual installation steps below, set **both** the .NET executable and CLI assembly in the plugin settings.

## Install for local testing

1. Publish the local CLI using the repository's pinned .NET 10 SDK, outside the Obsidian vault:
   ```sh
   dotnet publish tools/Praxis.VaultCli/Praxis.VaultCli.csproj -c Release -o /path/outside/vault/praxis-cli
   ```
2. Create an **existing private archive directory outside the vault**. Keep it on storage you control; archives contain full note and attachment contents, including hidden configuration, **without encryption**.
3. Build the self-contained plugin package outside the source directory (Node.js is needed only to package the plugin):
   ```sh
   node integrations/obsidian/scripts/package.js /path/outside/vault/praxis-vault
   ```
   The output contains only `main.js` and `manifest.json`. The packager refuses an existing destination and does not overwrite previous builds. It uses only built-in Node.js modules and embeds the repository's three local runtime modules into `main.js`; there are no downloaded npm dependencies.
4. Copy the **two generated files** into a folder named `praxis-vault` under Obsidian's configured plugins directory (its configuration directory may differ from `.obsidian`). Do not install the three unbundled source modules as the plugin.
5. Enable the plugin under Obsidian desktop's Community plugins. In Praxis Vault settings, specify the **absolute** archive directory, **absolute** published `Praxis.VaultCli.dll` path, and **absolute** `dotnet` executable path (for example from `command -v dotnet`).

The package can be installed manually, but is not published to the Obsidian community directory. Obsidian desktop and the local .NET runtime are required; the package is **not** a standalone replacement for the CLI. The generated entry is syntax-checked and loaded against a simulated Obsidian host in CI; **real desktop-app smoke testing is still outstanding** under issue #22.

## Commands and state

- **Capture or update local archive:** First run performs `capture --track-links`, verifies its new archive, and atomically records the current filename. Later runs perform `update` against that baseline, then verify and advance the pointer only after success. Every snapshot is a new file; old snapshots remain unchanged.
- **Preview local archive update:** Runs `plan` against the current snapshot without advancing state, showing added/updated/renamed counts and individual removed-reference IDs in a scrollable dialog.

The plugin records individual Obsidian **file** rename events and **folder** move events into explicit mappings. File-only chains and swaps retain their existing reconciliation behavior; folder events are applied in recorded order against paths in the previous archive. No identity is guessed from contents. Pending mappings and the current archive pointer are stored in `.praxis-vault-session.json` in the external archive directory, not under the vault. The plugin writes only its ordinary Obsidian settings inside the vault; there are no automatic note edits or recursive capture loops.

A partial capture, failed CLI invocation, interrupted process, malformed session state, missing prior archive, ambiguous/missing embed, unapproved reference removal or unsupported old-file disappearance **does not advance the current archive pointer**. The last good archive remains intact. If a verified archive was created but not adopted, it remains in the external directory for manual inspection.

The plugin does not approve disappearing tracked references. Use the CLI `plan` and `--approve-unlinks` with an exact list of note/attachment IDs when appropriate, following [ADR 0012](../../docs/decisions/0012-vault-reference-provenance.md). Manual CLI updates do not automatically advance the plugin's pointer; reconciliation must use an explicitly reviewed baseline. Folder moves reported by Obsidian can now retain nested note and attachment identities; missed or overlapping events, renames made while the plugin was disabled or before event registration, and new-file rename events not tied to a previous baseline may require manual mapping and will otherwise fail closed. This initial adapter does not guarantee consistency if another process modifies files without producing Obsidian events.

## Concurrency and failure recovery

Only one adapter action is run at a time in a plugin instance. An exclusive `.praxis-vault-session.lock` file prevents concurrent processes from changing the same external session state during an operation. An unexpected shutdown may leave this lock file. **Check that no archive process is active and preserve the state and archives before removing it manually.** The adapter does not silently delete stale locks or reset corrupted state.

Running or previewing a snapshot during Obsidian file change events invalidates the current operation; changes made through other processes without vault events cannot be detected reliably by this bridge. The CLI retains its own bounded path, size, version and integrity checks. Every spawned CLI process uses an argument array without a shell, has bounded output and a timeout, and is cancelled when the plugin unloads. Unload invalidates pending capture adoption and suppresses late success messages. Reconfiguring the archive directory or CLI path invalidates pending session initialization; the old session cannot silently become active after settings change.

Disable or uninstalling this adapter leaves all original vault files readable and editable in Obsidian. Its external archives and external session metadata are not deleted. **No mobile compatibility, automatic remote sync, encrypted storage, **real-app validation of folder event ordering**, or complete Markdown link parsing is claimed.**

## Automated verification

```sh
node --test integrations/obsidian/test/archive-session.test.js
node --check integrations/obsidian/main.js
node --check integrations/obsidian/archive-session.js
node --check integrations/obsidian/cli-process.js
node integrations/obsidian/scripts/package.js /path/outside/vault/praxis-vault
node --check /path/outside/vault/praxis-vault/main.js
node --test integrations/obsidian/test/packaged-plugin.test.js
```

The standard-library tests exercise session behavior against a simulated CLI on all three CI platforms. Packaging tests build the two-file installable layout, verify reproducibility and safety, and load the generated plugin against a simulated Obsidian host to exercise event registration and commands. Linux additionally runs `node --test integrations/obsidian/test/archive-session-cli.test.js` against the real compiled `.NET` CLI, covering initial capture, tracked-link preservation, rename/update, round-trip restore and refusal to publish an unapproved unlink. The independent CLI smoke in `scripts/release/smoke_vault_cli.py` also remains. These automated tests do **not** replace real Obsidian desktop validation.
