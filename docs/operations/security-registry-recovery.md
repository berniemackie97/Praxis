# Security registry recovery checkpoint

**Scope:** [#40](https://github.com/berniemackie97/Praxis/issues/40), following the device/security protocol in [#15](https://github.com/berniemackie97/Praxis/issues/15). This is an **offline, owner-operated identity-registry checkpoint**, not a complete disaster-recovery or scheduled backup system.

## Preserved records and verification

The host identity registry is a separate `identity.sqlite` database, distinct from the source catalog. It holds installation identity, principals, enrolled devices, approval records, security-space grants and source memberships, signed key epochs, per-device encrypted key envelopes, encrypted recovery envelopes, and recovery enrollment metadata. A catalog-and-originals snapshot alone does **not** preserve this data.

`SecurityRegistrySnapshots.BackupExistingAsync` requires an existing current-schema registry, acquires its exclusive lease, and copies a coherent committed SQLite view. It refuses a missing or old-schema identity registry instead of silently creating or migrating one. A new private staging directory contains the standalone database plus a versioned manifest with installation ID, schema version, byte count and SHA-256. Independent digest, schema, foreign-key, SQLite integrity and exact-file inventory verification must pass before the backup is published.

`VerifyAsync` rejects missing, incomplete, linked, unexpectedly populated, modified, incompatible, over-budget and exposed Unix-permission backups. `RestoreAsync` independently validates the backup, copies it to fresh private staging, rechecks it, and publishes a usable `identity.sqlite` only to a **nonexistent** destination. It never replaces a live registry. An interrupted operation may leave a named `.incomplete-` sibling; it must not be promoted or automatically deleted.

## Operator rehearsal

Stop the Praxis worker/host that uses the identity registry. Choose owner-approved **private** directories with enough capacity and unique new output paths. These examples use a **1 GiB per-operation database budget**, not a verified production sizing target.

```sh
dotnet run --project src/Praxis.Worker -c Release --no-build -- \
  recovery identity-backup /absolute/private/identity \
  /absolute/private/backups/identity-2026-11-10 --max-bytes 1073741824

dotnet run --project src/Praxis.Worker -c Release --no-build -- \
  recovery identity-verify /absolute/private/backups/identity-2026-11-10 \
  --max-bytes 1073741824

dotnet run --project src/Praxis.Worker -c Release --no-build -- \
  recovery identity-restore /absolute/private/backups/identity-2026-11-10 \
  /absolute/private/rehearsals/identity-test-01 --max-bytes 1073741824
```

Successful commands report installation ID, verified database length and SHA-256. The restored path contains a **separate copy** of the same installation, for isolated verification—not another authoritative running host. Verify grants, device enrollment and encrypted epochs using independently held owner keys.

Test corruption only on **disposable copies** of a verified backup: remove its manifest, truncate the database, insert unexpected entries or expose its permissions. Verify and restore must fail without producing a completed recovery root. Preserve the original and the error output. Automated `SecuritySnapshotTests` exercise these logical cases.

## Exclusions and outstanding gates

- **No private keys or secrets are recreated.** Device private signing/wrapping keys, operator CA private key, independent recovery signing key, AES-GCM content recovery secret, locally pinned client trust state and OS keychain secrets are not stored in the host registry. Loss can make encrypted content irrecoverable.
- **No coherent cross-store recovery set yet.** `catalog.sqlite`, immutable source originals, app configuration and future encrypted sync payloads are outside this snapshot. Backups taken at unrelated times cannot be called one consistent recovery point.
- **Hashing is not proof of authenticity or freshness.** A malicious actor who replaces both manifest and database, or supplies an older internally valid backup, is not defeated by SHA-256. Independently pinned signing identities and client epoch floors still apply.
- **No scheduling, retention, off-device encryption, restore SLA, durability or physical-volume claim.** Unix owner-only modes are enforced, while Windows ACLs require an owner inspection. Actual unplug/power-loss, disk-full, permission fault and representative restore-time measurements remain unverified.

Before #40 can close, Praxis needs a coordinated catalog/originals/registry/client-secret/config recovery set at an approved boundary, rehearsals across corrupt/partial/interrupted copies and physical storage faults, and an owner-approved retention and recovery policy. Hardware evidence remains under [#128](https://github.com/berniemackie97/Praxis/issues/128), with broader operations under [#65](https://github.com/berniemackie97/Praxis/issues/65). No paid services or remote access are authorized by this step.

## Local verification

```sh
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test tests/Praxis.Storage.Tests/Praxis.Storage.Tests.csproj \
  -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~SecuritySnapshotTests|FullyQualifiedName~SecurityRegistryTests|FullyQualifiedName~SpaceKeyDistributionTests'
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
```
