# Physical storage recovery acceptance — #128

**Status:** Opt-in verification harness proposed. No physical results have been recorded, and no GitHub-hosted or self-hosted CI is available in the current delivery configuration. This document is not a passing test report.

## Boundary

The production [coherent snapshot contract](../decisions/0018-coherent-local-backup.md) is already covered by small deterministic tests. This separate tool exercises real filesystem I/O beyond 2 GiB, without creating a huge byte array or modifying any existing Praxis catalog. It is **only** an operator-run verification path.

The tool creates one unique, private `praxis-recovery-<guid>` directory inside an **existing, operator-selected scratch root**. It never overwrites that directory, existing sources, or existing backup destinations. It does not delete the workspace automatically. Before writing, it requires a supported mounted filesystem with at least the provisioned capacity (more than 9 GiB of available space).

It creates a deterministic 2 GiB + 4 KiB stream and a separate small source, captures both through `LocalCatalog`, backs up and independently verifies the committed SQLite/WAL catalog plus referenced original bytes, modifies the small live source **after** snapshot publication, restores to a new directory, and verifies:

- Original large object size/digest, source/item identity and committed request receipt after restore.
- The two original objects, exact snapshot catalog checksum, total bytes and object count.
- Historical snapshot isolation: the restored small item has one version, not the live-only second revision.
- Repeated on-demand verification after a real drive disconnect/reconnect, using `evidence.json` in the scratch workspace.

The evidence JSON contains test identifiers and checksums, **not personal source data or credentials**. Directory paths appear in local console output; redact them before adding logs to a public issue.

## Run the large-file and fresh-restore acceptance on macOS

Use a **dedicated scratch directory** on the test volume. Never point this command at a real Praxis data directory, its `objects` folder, or a directory containing personal documents. The selected path must already exist and have suitable filesystem permissions. Example (replace the placeholder with a real scratch directory on the intended volume):

```sh
mkdir -m 700 "/Volumes/YOUR_DRIVE/PraxisRecoveryQA"
cd /path/to/Praxis
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet run --project tools/Praxis.RecoveryCheck -c Release \
  --no-build --no-restore -- --create "/Volumes/YOUR_DRIVE/PraxisRecoveryQA"
```

The command prints the exact new workspace path, e.g. `.../PraxisRecoveryQA/praxis-recovery-<guid>`. Keep it. It should finish only after a real verified >2 GiB backup and recovery. The stdout success message is not sufficient evidence if the process exits with an error.

Inspect filesystem type, permissions and physical storage with `diskutil info` and `stat` (macOS). Record the operating-system version, filesystem, exact command, exit code, time of test, verification report and observed `evidence.json` digest, without publishing private mount paths or source content.

### Reconnect test

Once the **completed** check has exited and the external volume is idle, eject/unmount the test drive normally, disconnect and reconnect it, then run:

```sh
dotnet run --project tools/Praxis.RecoveryCheck -c Release \
  --no-build --no-restore -- --verify "/Volumes/YOUR_DRIVE/PraxisRecoveryQA/praxis-recovery-YOUR_ID"
```

This rehashes the backup and both restored originals and checks that the historical receipt/source version is unchanged. Record success/failure separately from the original creation run.

Do **not** physically disconnect a drive while it is in use unless using an isolated, disposable test volume and an explicit controlled fault plan. Power loss and filesystem metadata durability are not proven merely by a clean disconnect/reconnect. No automatic physical-unplug test is claimed.

## Negative and failure acceptance still required

The following must be conducted on a **disposable, quota-limited or dedicated** volume, not by filling the owner's everyday system or source drive:

| Scenario | Setup and action | Expected evidence |
| --- | --- | --- |
| Disk exhaustion during backup | Use a deliberately small isolated volume for backup output, with the live catalog and last completed backup stored elsewhere. Interrupt while copying a large original through a controlled mount/quota. | No completed backup destination published; previous backup verifies; live catalog still opens and original hashes match; an `.incomplete-` directory may remain and is not treated as a usable backup |
| Disk exhaustion during restore | Use a completed verified backup on a healthy volume and an isolated insufficient-capacity destination volume. | No completed restored destination; healthy backup and existing catalog are unchanged; no incomplete directory is advertised as complete |
| Permission failure | On an isolated test destination, revoke write permission before publication or transfer. | Explicit error; no completed output; existing source/backup unchanged |
| Unexpected unmount | On an isolated removable test volume only, interrupt a running backup/restore operation, then reattach safely. | Explicit failure or interrupted process; no completed, verified destination for the interrupted operation; previously completed backup re-verifies on reconnect |
| Space and durability | Repeat successful create/reverify on the actual intended filesystem, capture APFS/other format and ACL/mode details. | Recorded hashes and receipts; no automatic claim that filesystem durability, encryption, device safety or Windows permissions are solved |

The regular unit suite already covers missing originals, corrupt manifest/objects, non-overwrite destinations, pre-cancel and logical budgets. Those tests **do not replace** an actual disk-full/unmount fault on target hardware. This PR does not add a fault-injection seam to the production storage library solely for a manual test.

## Completion gates and follow-up

The evidence above must be reviewed against #128 before closing it. The owner should attach the redacted test report and identify any inaccessible or unexpectedly altered paths, partial staging, OS/volume behavior and physical device details. An unsuccessful or interrupted run is **not** automatically a storage defect; root cause needs concrete failure evidence.

#128 remains open until its actual physical failure cases and multi-GiB restore succeed. #100 and #107 remain dependency-blocked by #128/#100 respectively. Cross-platform CI and final integrated Inbox/restore acceptance are still separate requirements under #137/#130. The approved decision to avoid GitHub Actions charges does not allow us to mark unexecuted checks green.
