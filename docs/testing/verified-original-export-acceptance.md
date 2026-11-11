# Verified-original export — owner macOS acceptance (#166)

**Status:** implementation under review; the commands below are a manual procedure, not test results. Do not use an existing personal Praxis catalog for destructive or path-replacement scenarios.

## Scope

Test the **Inbox & Library → source detail → Export original…** operation with a disposable owner-private catalog and approved scratch Inbox. The first-party Save panel chooses the output name; the inherited-pipe worker resolves the active item and latest revision, reads from immutable object storage, verifies the source and staged output SHA-256, and publishes without replacing an existing destination. It never reads the watched path to produce the export. An unsupported binary is a valid export input.

The worker's output is a private plaintext copy owned by the user. A directory synchronized by iCloud or another service may upload it independently; Praxis makes no network request and does not grant agent access. The Save panel may offer to replace an existing file, but the worker **always refuses** to overwrite it.

## Isolated preparation

Quit Praxis with ⌘Q and preserve any existing preview app settings **before** choosing test paths. The preview bundle identifier is \`dev.praxis.renames-preview\`; it can reopen its last configured catalog.

In zsh, use the following in one Terminal session. The test root is unique and **not** a repository or external development drive.

\`\`\`zsh
setopt interactivecomments
export PRAXIS_EXPORT_TEST="$(mktemp -d "$HOME/Praxis-Export-QA.XXXXXXXX")"
export PRAXIS_PREF_DOMAIN="dev.praxis.renames-preview"
chmod 700 "$PRAXIS_EXPORT_TEST"
mkdir -m 700 "$PRAXIS_EXPORT_TEST/CatalogParent" "$PRAXIS_EXPORT_TEST/Inbox" "$PRAXIS_EXPORT_TEST/Exports" "$PRAXIS_EXPORT_TEST/HeldOutsideInbox"
printf 'Praxis disposable export QA\n' > "$PRAXIS_EXPORT_TEST/.praxis-export-test-owned"
if defaults read "$PRAXIS_PREF_DOMAIN" >/dev/null 2>&1; then
  defaults export "$PRAXIS_PREF_DOMAIN" "$PRAXIS_EXPORT_TEST/preferences-before.plist" || exit 1
  printf 'present\n' > "$PRAXIS_EXPORT_TEST/preferences-before.state"
else
  printf 'absent\n' > "$PRAXIS_EXPORT_TEST/preferences-before.state"
fi
printf 'Workspace: %s\n' "$PRAXIS_EXPORT_TEST"
\`\`\`

Start the **current branch's** packaged/Xcode Praxis.app, configure \`CatalogParent\` as the catalog parent and \`Inbox\` as the primary watched Inbox, and start the worker. Do not select \`Exports\` as a watched root. Only launch an app built for this code revision.

## Acceptance matrix

| Case | Owner action and independent evidence | Required outcome |
| --- | --- | --- |
| Binary fidelity | Create \`Inbox/first.bin\` with \`printf '\000\377\001\002' > "$PRAXIS_EXPORT_TEST/Inbox/first.bin"\`, wait for capture, use Export original… to \`Exports/first-copy.bin\`. Run \`shasum -a 256\` on both. | Exact bytes, length, SHA-256 receipt, no conversion or text-extraction prerequisite; inspector selection remains usable |
| Detached original | Once captured, run `mv "$PRAXIS_EXPORT_TEST/Inbox/first.bin" "$PRAXIS_EXPORT_TEST/HeldOutsideInbox/first.bin"` to remove the watched path without touching the catalog; export again under a **new** filename while the catalog source remains active. | Export succeeds from canonical object storage without opening watched bytes; inspect the source status/identity, not the current watcher filename |
| No overwrite | Save again under an already exported filename, including any Save panel replacement confirmation. Independently hash the existing destination before/after. | Denial with visible error and byte-for-byte unchanged existing file |
| Current revision | Modify captured \`first.bin\` to new bytes, let intake reconcile, select the now-current revision and export with another name; try an earlier revision through the protocol regression suite. | Current bytes exported, earlier revision rejected, no duplicate logical source receipt |
| Revocation | Revoke a disposable source in the UI and attempt export; use the protocol negative test for the exact revoked item (the UI may disable export). | Denial, no new output, old canonical original preserved |
| Corruption | Run the automated \`OriginalExportTests\` with deliberate corruption of a **disposable** original object. Never mutate a real catalog's object tree. | No published output; staging cleaned; original content is not modified by export |
| Symlink alias and race | Run deterministic \`OriginalExportTests\` for catalog symlinks, late-created destination symlink and pinned-directory rename/swap between staging and publication. | No writes into private catalog; no replacement of existing bytes; no leftover staging file |
| Restart and cleanup | Quit, reopen the same test catalog and re-export to a fresh filename. Compare hashes and check the source ID/revision. Inspect \`Exports\` for \`.praxis-export-*.part\`. | Same source identity and bytes after restart; no abandoned staging files |
| Disposable physical volume | On an optional scratch APFS disk image, export through the Save panel to a directory on the image. Test a normal detach/reconnect *between* exports, then a failure scenario during an export if safely reproducible. | No false success or original mutation; clean retry to a different available destination. Never detach a personal external drive under active I/O |

Record the exact commit SHA, SDK/Xcode/macOS versions, \`TEST SUCCEEDED\`/Release test results, the UI's success/error state, source IDs and digests, target filesystem type, and any retained staging files. Avoid posting raw personal paths or binary contents to public GitHub issues.

## Code validation

\`\`\`bash
git rev-parse HEAD
git status --short
dotnet restore Praxis.slnx --locked-mode &&
dotnet build Praxis.slnx -c Release --no-restore &&
dotnet test Praxis.slnx -c Release --no-build --no-restore &&
dotnet format Praxis.slnx --verify-no-changes --no-restore &&
scripts/macos/build-desktop.sh --archive-existing &&
scripts/macos/test-worker-session.sh
echo "Verification exit code: $?"
\`\`\`

Quit any running Praxis preview before rebuilding. The test runner is app-hosted and must not load previously configured owner data. Check the native test \`.xcresult\`; do not equate a queued GitHub Actions job with passing macOS tests.

## Cleanup and limitations

Quit Praxis with ⌘Q. Verify the root marker and mounted volumes before removing **only the unique scratch root**:

\`\`\`zsh
if pgrep -x Praxis >/dev/null; then
  echo 'Quit Praxis with Command-Q before cleanup'
else
  case "$PRAXIS_EXPORT_TEST" in
    "$HOME"/Praxis-Export-QA.*)
      if [[ -f "$PRAXIS_EXPORT_TEST/.praxis-export-test-owned" ]]; then
        if [[ "$(cat "$PRAXIS_EXPORT_TEST/preferences-before.state")" == "present" ]]; then
          defaults import "$PRAXIS_PREF_DOMAIN" "$PRAXIS_EXPORT_TEST/preferences-before.plist" || exit 1
        else
          defaults delete "$PRAXIS_PREF_DOMAIN" 2>/dev/null || true
        fi
        if mount | grep -F " on $PRAXIS_EXPORT_TEST/" >/dev/null; then
          echo 'A disposable volume is mounted inside the workspace. Detach it before deleting.'
        else
          rm -rf -- "$PRAXIS_EXPORT_TEST"
          unset PRAXIS_EXPORT_TEST PRAXIS_PREF_DOMAIN
        fi
      fi
      ;;
    *) echo 'Refusing cleanup: unexpected workspace path' ;;
  esac
fi
\`\`\`

Do **not** run \`git clean\`, \`git reset --hard\`, or broad \`rm -rf ~/Praxis*\`. Verify the original app preferences on next launch. The private catalog and original object files in the scratch directory are disposable only after results are recorded.

This issue covers owner-local export and a pinned directory on macOS/Linux. On Windows the existing managed non-overwrite publication path remains; a hostile cross-process directory replacement on that platform is **not** established as safe without an independently tested handle-relative implementation. External-volume interruption, Windows fault matrix and three-OS CI remain release-hardening gates under #130/#137, not silent passes of #166.
