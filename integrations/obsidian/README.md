# Praxis vault adapter for Obsidian desktop

This is the first **local, user-triggered desktop adapter** for Praxis. It uses the existing .NET vault CLI to capture, preview, update and verify a vault archive. It does **not** synchronize to a service, watch files in the background, rewrite notes, delete vault files or create backups automatically.

## Install for local testing

1. Publish the local CLI using the repository's pinned .NET 10 SDK, outside the Obsidian vault:
   ```sh
   dotnet publish tools/Praxis.VaultCli/Praxis.VaultCli.csproj -c Release -o /path/outside/vault/praxis-cli
   ```
2. Create an **existing private archive directory outside the vault**. Keep it on storage you control; archives contain full note and attachment contents, including hidden configuration, **without encryption**.
3. Copy `main.js`, `archive-session.js`, `cli-process.js` and `manifest.json` into the vault's configured Obsidian plugins directory under a folder named `praxis-vault`. The configuration directory is not necessarily `.obsidian`.
4. Enable the plugin under Obsidian desktop's Community plugins. In Praxis Vault settings, specify the **absolute** archive directory and **absolute** published `Praxis.VaultCli.dll` path.

This first adapter is not yet distributed as an installer or published to the Obsidian plugin directory. It requires a supported desktop Obsidian app, the local .NET runtime and files copied manually. **Real desktop-app smoke testing is still outstanding** under issue #22.

## Commands and state

- **Capture or update local archive:** First run performs `capture --track-links`, verifies its new archive, and atomically records the current filename. Later runs perform `update` against that baseline, then verify and advance the pointer only after success. Every snapshot is a new file; old snapshots remain unchanged.
- **Preview local archive update:** Runs `plan` against the current snapshot without advancing state, showing added/updated/renamed counts and individual removed-reference IDs in a scrollable dialog.

The plugin records individual Obsidian **file** rename events into explicit one-to-one mappings, including chained moves and swaps. Pending mappings and the current archive pointer are stored in `.praxis-vault-session.json` in the external archive directory, not under the vault. The plugin writes only its ordinary Obsidian settings inside the vault; there are no automatic note edits or recursive capture loops.

A partial capture, failed CLI invocation, interrupted process, malformed session state, missing prior archive, ambiguous/missing embed, unapproved reference removal or unsupported old-file disappearance **does not advance the current archive pointer**. The last good archive remains intact. If a verified archive was created but not adopted, it remains in the external directory for manual inspection.

The plugin does not approve disappearing tracked references. Use the CLI `plan` and `--approve-unlinks` with an exact list of note/attachment IDs when appropriate, following [ADR 0012](../../docs/decisions/0012-vault-reference-provenance.md). Manual CLI updates do not automatically advance the plugin's pointer; reconciliation must use an explicitly reviewed baseline. Folder renames, renames made while the plugin was disabled or before event registration, and new-file rename events not tied to a previous baseline may require manual rename mappings and will otherwise fail closed. This initial adapter does not guarantee consistency if another process modifies files without producing Obsidian events.

## Concurrency and failure recovery

Only one adapter action is run at a time in a plugin instance. An exclusive `.praxis-vault-session.lock` file prevents concurrent processes from changing the same external session state during an operation. An unexpected shutdown may leave this lock file. **Check that no archive process is active and preserve the state and archives before removing it manually.** The adapter does not silently delete stale locks or reset corrupted state.

Running or previewing a snapshot during Obsidian file change events invalidates the current operation; changes made through other processes without vault events cannot be detected reliably by this bridge. The CLI retains its own bounded path, size, version and integrity checks. Every spawned CLI process uses an argument array without a shell, has bounded output and a timeout, and is cancelled when the plugin unloads.

Disable or uninstalling this adapter leaves all original vault files readable and editable in Obsidian. Its external archives and external session metadata are not deleted. **No mobile compatibility, automatic remote sync, encrypted storage, folder-event parity or complete Markdown link parsing is claimed.**

## Automated verification

```sh
node --test integrations/obsidian/test/archive-session.test.js
node --check integrations/obsidian/main.js
node --check integrations/obsidian/archive-session.js
node --check integrations/obsidian/cli-process.js
```

These tests use the Node.js standard library and a simulated CLI. The real .NET CLI integration smoke remains in `scripts/release/smoke_vault_cli.py`. The tests do **not** replace manual verification in an actual Obsidian desktop installation.
