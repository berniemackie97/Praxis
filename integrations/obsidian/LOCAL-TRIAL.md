# Local Praxis trial on macOS

This procedure installs the **local Obsidian archive preview**, not a full Second Brain application. It does not enable automatic tagging, semantic search, AI processing, mobile sync, or network services.

## Prerequisites

- Obsidian desktop, with a **new disposable test vault** created and opened at least once. Note its absolute path. Do not test against your only copy of personal notes.
- This repository and the exact .NET SDK pinned in [`global.json`](../../global.json), plus a working `dotnet` executable and Node.js. Run the commands from the repository root in Terminal.
- A private archive directory **outside** the vault. Archives are plaintext and can contain hidden Obsidian settings and other personal data.

For example:

```sh
dotnet --version
node --version
command -v dotnet

mkdir -p "$HOME/PraxisLocalArchives"
dotnet publish tools/Praxis.VaultCli/Praxis.VaultCli.csproj -c Release \
  -o "$HOME/PraxisLocalCli"
```

The published assembly is `$HOME/PraxisLocalCli/Praxis.VaultCli.dll`. The archive directory remains empty until an actual plugin capture; the installer runs its smoke check only on an isolated temporary fixture.

## Install without overwriting existing plugins

Replace the vault path below with the actual location of the newly created Obsidian test vault:

```sh
node integrations/obsidian/scripts/install-local.js \
  --vault "/absolute/path/to/your-test-vault" \
  --archives "$HOME/PraxisLocalArchives" \
  --cli "$HOME/PraxisLocalCli/Praxis.VaultCli.dll" \
  --dotnet "$(command -v dotnet)"
```

If your vault uses a non-default Obsidian config directory, add `--config-dir <directory-name>` (for example, `--config-dir .config-obsidian`). Supply the configured directory's name, **not** a filesystem path.

Before installing anything into the vault, the installer uses a disposable Markdown note and binary attachment to test the **actual .NET CLI**: capture with link tracking, verify, inspect links, and restore byte-for-byte. Only then does it build the two-file plugin package and place `main.js`, `manifest.json`, and local path settings `data.json` under the test vault's plugin directory. It refuses an existing `praxis-vault` installation; it does not replace files or change an existing note. Failure during preflight leaves the vault unchanged.

The absolute `dotnet` path is saved to avoid relying on Finder-launched Obsidian inheriting Terminal's `PATH`.

## Verify in Obsidian desktop

1. Open the disposable test vault, enable community plugins if needed, then enable **Praxis Vault** under the installed community plugins.
2. Add a Markdown note and, optionally, one small image with a simple attachment embed such as `![[picture.png]]`. Avoid private documents until you have verified your local backup behavior.
3. Run the command-palette action **Praxis Vault: Capture or update local archive**. Confirm a ZIP archive appears in the separate archive directory.
4. Edit the note and run **Praxis Vault: Preview local archive update**. Review the reported file changes.
5. Run **Praxis Vault: Capture or update local archive** again. Verify the newer archive was created without replacing the earlier one.
6. In the **disposable test vault**, move a folder containing a Markdown note and image using Obsidian's file explorer while the plugin is enabled. Preview then update again; the existing files should appear as renamed, not added. If the move is rejected, preserve the original archive and note exactly what Obsidian events were observed. This is a **manual acceptance test**, not a compatibility claim.
7. Test restore to a **new** directory, never your active vault:

   ```sh
   dotnet "$HOME/PraxisLocalCli/Praxis.VaultCli.dll" verify \
     "/absolute/path/to/PraxisLocalArchives/<verified-archive>.zip"

   dotnet "$HOME/PraxisLocalCli/Praxis.VaultCli.dll" restore \
     "/absolute/path/to/PraxisLocalArchives/<verified-archive>.zip" \
     "$HOME/PraxisRecoveredTrial"
   ```

   Replace `<verified-archive>` with the actual generated filename. The restore target must not already exist. Compare the recovered Markdown and original attachments with the test vault. After the rehearsal, remove the disposable recovered copy only if you're satisfied with the result.

8. Disable the plugin and verify Obsidian can still open/edit the source notes. Re-enable it, run preview, and inspect the external archive session state. Do **not** remove an external session lock unless you confirm no operation is running.

## Current limits

The adapter is **explicit capture only**, not a watcher or live synchronization engine. It records file and folder rename events while loaded, and the archive CLI can reconcile ordered folder moves across existing records. Missed events, mixed or conflicting renames, and moves made with the plugin disabled can still require explicit manual mappings and otherwise fail closed. A removed tracked embed can require explicit note/attachment-ID approval in the CLI; the plugin will not silently approve it. An unresolved or ambiguous supported link blocks tracked capture. It cannot guarantee a consistent snapshot when external processes write directly to the vault while capture is running.

Plugin installation and command behavior have automated tests, but this procedure has **not yet been run against a real Obsidian desktop session in CI**. Record the Obsidian version, OS, paths, observed behavior and restore comparison when doing the manual acceptance test. Do not mark [#22](https://github.com/berniemackie97/Praxis/issues/22) or [#25](https://github.com/berniemackie97/Praxis/issues/25) complete until those results are reviewed. No mobile or remote processing is provided; [#50](https://github.com/berniemackie97/Praxis/issues/50) remains the trust/hosting decision gate.
