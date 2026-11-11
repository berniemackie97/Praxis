# Offline coordinated recovery set

**Scope:** #40 — one quiescent recovery point for the owner-local catalog, its referenced immutable originals, and the host identity registry. This does not automatically preserve owner-held credentials or unrelated application settings.

## Recovery point contract

`LocalRecoverySet.BackupExistingAsync` acquires the ordinary exclusive lease on **both** source roots before copying either database. Stop the Praxis worker and any host using these roots first. The catalog and identity directory must be independent and must not contain each other. No normal Praxis writer can continue through the lease while the recovery point is built. Processes that bypass Praxis's exclusive leases or external writers are outside this contract.

The catalog backup uses SQLite's backup API and copies all catalog-recorded immutable originals. The identity backup takes a separate SQLite backup and preserves installation identity, grants, signed key epochs and encrypted envelopes. Both child snapshots retain their existing independent manifests and checks. The recovery-set manifest binds the **actual verified child reports** (object inventory size, total length and catalog SHA-256; registry installation ID, length and SHA-256). One positive total-byte budget covers both components. The complete set becomes visible at its requested fresh path only after both child backups and their aggregate manifest verify.

The published set has exactly:

```text
recovery-set/
  manifest.json
  catalog/
    manifest.json
    catalog.sqlite
    objects/
  identity/
    manifest.json
    identity.sqlite
```

Files under `catalog/objects` are digest-sharded. Exact object inventory is derived from the catalog; there may be other required structure in that subtree. Backup verification refuses missing/extra/link entries, incompatible formats, altered bytes, invalid schemas/foreign keys, mismatched manifest fields and incomplete staging. On Unix, backup roots and files require owner-private modes.

`LocalRecoverySet.RestoreAsync` independently verifies the entire published set before creating output staging, restores both components with their existing independent integrity checks, opens the recovered identity registry to confirm its installation ID, and promotes one parent directory containing only `catalog/` and `identity/`. It refuses an existing destination. A failure cannot expose a completed destination, but may leave a private `.incomplete-` directory for manual inspection. It never overwrites the live catalog, registry or previous backups. The restored directory is a **recovery rehearsal output**, not a replacement for the configured application paths.

Because both leases are held throughout capture, this constitutes a **consistent quiescent-host recovery boundary**. It does **not** mean two independent backups collected at arbitrary times are safe to pair, nor does it prove filesystem durability after power loss. Restore must be verified on the target filesystem before relying on the checkpoint.

## Disposable operator rehearsal (recommended for #40)

The existing `Praxis.RecoveryCheck` tool can now create an entirely **synthetic** catalog and client-held security registry on an owner-approved scratch filesystem, exercise the actual `LocalRecoverySet` backup/verification/fresh-root restore APIs, and persist a **non-secret evidence file** for verification by another invocation. It never opens the configured Praxis catalog or existing security registry. It generates temporary device signing, wrapping and recovery factors in memory, checks that the recovered encrypted key epoch unlocks with the right factor and rejects a different factor, then discards those secrets. It also checks restored source IDs, original bytes, version history, member permission, epoch metadata, and identity continuity.

Create a **new, disposable** scratch directory on the actual filesystem you want to validate. On a Mac with an approved, mounted `EnderChest` volume, for example:

```sh
umask 077
mkdir -p /Volumes/EnderChest/Praxis-Recovery-Lab
chmod 700 /Volumes/EnderChest/Praxis-Recovery-Lab

dotnet run --project tools/Praxis.RecoveryCheck -c Release -- \
  --set-create /Volumes/EnderChest/Praxis-Recovery-Lab
```

The command prints a newly created `praxis-set-rehearsal-<random>` workspace. **Keep the printed path** and run an independent verification (a separate process) against it:

```sh
dotnet run --project tools/Praxis.RecoveryCheck -c Release -- \
  --set-verify /Volumes/EnderChest/Praxis-Recovery-Lab/praxis-set-rehearsal-<printed-id>
```

For a disposable end-to-end process check with automatic cleanup, run `python3 scripts/release/smoke_recovery_set.py` after a locked Release build. It exercises `--set-create`, then a separate `--set-verify` invocation, enumerated failure-boundary evidence and malformed-evidence refusal. The generated synthetic evidence is removed automatically; **use the manual steps above when a persistent operator audit record is needed**. On failure, the standalone tool emits only a fixed failure category, not a raw exception, path or stack trace. It does not prove lost-device key custody, drive power loss or backup confidentiality.

Both commands must exit **0**. Keep the entire workspace and `evidence.json` for inspection; the tool does **not** delete or overwrite the workspace. The evidence records only generated test IDs, original content hashes, component snapshot reports and creation time; it does **not** store private keys, recovery secrets, real user documents or original production credentials. The test data and its on-disk manifests are nevertheless plaintext, so keep the scratch root private.

The creation command exercises these actual storage boundaries on **disposable test data**: locked source rejection, cancelled backup, logical byte-budget limit, existing restore destination protection, owner-private manifest enforcement on Unix, missing identity component, truncated SQLite recovery bytes and denial of publishing a damaged set. It deliberately changes the live synthetic catalog/registry after backup to establish that the restored version and grants came from the earlier checkpoint. It does **not** forcibly fill the real volume, eject the SSD or kill a process during writes. Those are separate physical fault/reconnection exercises and should be performed only on an independently approved disposable volume. The existing `--create`/`--verify` commands in this tool remain the real **>2 GiB** catalog/original-file test for [#128](https://github.com/berniemackie97/Praxis/issues/128), with their own free-space requirement.

**Acceptance evidence to retain:** printed workspace path (you may redact your username/home directory when sharing), the two complete command exit codes, reported backup catalog/object and registry digests, host OS/filesystem/mount state, and results of independent `--set-verify`. Do **not** upload `identity.sqlite`, real private keys, the scratch set itself or a production backup. This limited #40 rehearsal demonstrates the coordinated recovery pipeline and safe error boundaries; it is **not** evidence of whole-system disaster recovery, backup encryption, lost-device key recovery, a power-loss guarantee, or a retention/SLA policy.

## Local owner rehearsal

Use the actual absolute existing catalog and registry directories, only after shutting down their owning processes. Never experiment on the only valid backup or a live data directory. Choose an existing owner-private parent on disposable storage or a noncritical test folder. Set a budget large enough for the recorded catalog and originals plus the identity database.

```sh
umask 077
mkdir -p "$HOME/Praxis-Recovery-Rehearsal"
chmod 700 "$HOME/Praxis-Recovery-Rehearsal"
ROOT="$HOME/Praxis-Recovery-Rehearsal"
CATALOG="/absolute/existing/catalog-directory"
IDENTITY="/absolute/existing/identity-directory"
BUDGET=10737418240

dotnet run --project src/Praxis.Worker -c Release --no-build -- \
  recovery set-backup "$CATALOG" "$IDENTITY" "$ROOT/backup-01" --max-bytes "$BUDGET"

dotnet run --project src/Praxis.Worker -c Release --no-build -- \
  recovery set-verify "$ROOT/backup-01" --max-bytes "$BUDGET"

dotnet run --project src/Praxis.Worker -c Release --no-build -- \
  recovery set-restore "$ROOT/backup-01" "$ROOT/restored-01" --max-bytes "$BUDGET"
```

All commands must return zero. The emitted report must give equal catalog SHA-256, object count/total bytes, and identity installation ID/digest/length across backup, verify and restore. Check that the restored set contains only `catalog/` and `identity/` and that the recovered catalog's original receipts, source/item IDs and revisions match the expected checkpoint, while signed device permissions and key epochs still work with independently retained credentials. Do not launch the recovered identity registry as a second active authoritative installation on the network.

### Negative and interruption rehearsal

On a **disposable copy** of a successfully verified set, independently try a removed identity manifest, corrupted identity database, truncated original object, edited top-level manifest, extra entry, or an unreadable backup directory. `set-verify` and `set-restore` must return nonzero, and the destination must remain absent. Do not corrupt the original verified backup. A request targeting an existing destination containing an important sentinel file must be rejected without altering that file. Run a pre-cancelled or deliberately interrupted command and verify its partially built `.incomplete-` sibling is never accepted by `set-verify`. Remove incomplete artifacts only after confirming an independently verified completed checkpoint exists.

For actual removable drive evidence, record the real filesystem, capacity, OS, command exit codes, backup digests, successful reconnect and independent verification, plus what happened with a **disposable test volume** during disk-full, permission denial and unexpected unmount. Do **not** force-unmount a volume containing the only copy of data. The separate >2 GiB original/media rehearsal remains required by [#128](https://github.com/berniemackie97/Praxis/issues/128). This document is not evidence those hardware scenarios were executed.

## Security and recoverability limits

This recovery set contains **sensitive plaintext original files** and a private registry with security metadata/encrypted key envelopes. It is neither encrypted-at-rest nor independently signed. SHA-256 alone cannot detect a malicious actor replacing both content and manifests or replaying an older internally valid set. Owner approval of backup storage/retention/encryption and offline custody is still required.

Owner-held recovery signing credentials, 32-byte content recovery secrets, private device signing/wrapping keys, operator issuer private keys, client trust pins/anti-rollback epoch floors, keychain items, machine-specific configuration, and any future encrypted sync payload stores are **not** captured. Losing them may render encrypted data unrecoverable even if both databases are perfect. Keep key material in an independently protected recovery process; do not print, upload or commit secrets as part of verification.

There is no automatic periodic schedule, encrypted off-device transfer, retention/rotation, Windows owner-ACL audit, drive power-loss guarantee, or proven RPO/RTO. Cross-platform CI under #137 remains unexecuted. #40 is not complete until the required physical/secret-factor recovery cases and owner-selected operations policy have evidence; feature #65 covers the broader operations release. No claims about whole-product disaster recovery follow from a local test pass.

## Source-level regression verification

```sh
dotnet restore Praxis.slnx --locked-mode &&
dotnet build Praxis.slnx -c Release --no-restore &&
dotnet test tests/Praxis.Infrastructure.Tests/Praxis.Infrastructure.Tests.csproj \
  -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~RecoverySetTests|FullyQualifiedName~CatalogSnapshotTests|FullyQualifiedName~SecuritySnapshotTests' &&
dotnet test Praxis.slnx -c Release --no-build --no-restore &&
dotnet format Praxis.slnx --verify-no-changes --no-restore
```
