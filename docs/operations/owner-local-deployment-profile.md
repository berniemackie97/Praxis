# Owner-local deployment profile and operations acceptance

**Scope:** #41, owner-local runtime only. This is a reproducible verification profile for the existing macOS-first desktop architecture, **not** approval to deploy a source-bearing HTTP service. It uses the layered backend and Generic Host from [ADR 0039](../decisions/0039-layered-architecture.md). The security constraints in [ADR 0021](../decisions/0021-owner-local-caller-security.md) still apply.

## Topology and ownership

| Boundary | Intended arrangement | Evidence / restriction |
| --- | --- | --- |
| First-party UI | One native SwiftUI process for the logged-in owner | Launches the .NET worker as a child, not a network service; native app sign/launch acceptance remains #215 |
| Worker | One unprivileged .NET 10 process, inherited stdin/stdout, stderr only for operational status | No TCP listener, no OS-wide daemon, no shared service account |
| Catalog | One owner-private root containing the SQLite database, originals and exclusive lock | Fresh `--stdio` with a missing root fails; an already-running catalog rejects another worker |
| Intake | One to four explicitly approved, nonoverlapping directories outside the catalog | Offline roots require reapproval; they are not replacement catalogs |
| Health host (optional) | A separate `Praxis.Api` instance on numeric IPv4 `127.0.0.1` | Fixed liveness/readiness only; no catalog, credentials, private routes or application transport |
| Recovery set | Quiescent catalog + identity-registry backup to a new owner-private destination | Plaintext sensitive backup; independently held keys and app settings are outside the set |

The owner chooses the actual paths and account. Do not install the worker as a privileged daemon, map its inherited protocol to a socket, share its catalog between user accounts, or expose the health listener through a tunnel or proxy. On Unix, the catalog directory must be owner-private; on Windows the application still needs an explicit effective-ACL audit. The operational smoke uses disposable directories, never an existing user's catalog.

## Reproduce and inspect

Install the SDK selected in `global.json`; `dotnet --version` should agree with that file. Run from a fresh checkout, with enough disk space for the published binaries and disposable SQLite catalogs:

```sh
git rev-parse HEAD
git status --short
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
python3 scripts/release/smoke_worker_protocol.py
python3 scripts/release/smoke_worker_lifecycle.py
dotnet publish src/Praxis.Api/Praxis.Api.csproj -c Release --no-build --no-restore -o artifacts/publish
python3 scripts/release/smoke_api.py
```

Every command must return zero; use `&&` or check individual exit codes before reporting a complete pass. The lifecycle smoke starts actual Release workers with a temporary owner-only root. It probes missing-catalog refusal, exclusive lease contention, stdout protocol isolation, normal EOF/restart, distinct per-run correlation IDs and ordered event sequences, unexpected root entries, invalid Unix permissions and symlinks (Unix only), an invalid SQLite image, and a future schema version without write-back. It checks that error diagnostics do not include the deliberately private path sentinel. The API smoke verifies the actual published process, loopback binding, absent private routes, wildcard override refusal and invalid-port startup.

`WorkerConsoleFormatter` emits timestamped UTC operational events in this shape:

```text
[2026-11-10T20:00:00.0000000+00:00] run=<opaque-id> seq=<event-number> Worker supervisor started.
```

The run identifier is generated for the process, not a user ID, device credential, source ID or persistent tracing key. `seq` is a per-process ordering aid, not a cross-device distributed trace. Process failures emit only enumerated categories; the native desktop protocol can return **private source-level errors** on stdout and must never be captured as a general-purpose log.

### Failure and recovery decisions

| Symptom | Required operator behavior |
| --- | --- |
| `directory_unavailable` | Stop. Reconnect the approved catalog volume or restore to an independently verified fresh root. Never replace a missing configured catalog with an empty one |
| `io_error` from a concurrent worker | Stop the extra worker. Preserve the original owner session; do not delete a lock file to force entry |
| `access_denied` | Verify intended OS owner and directory/file permissions. Do not run as administrator/root or broaden access as a workaround |
| `invalid_input` | Inspect the root's structure, links and configured folder identities privately. Do not delete unexpected files blindly |
| `storage_error` | Quiesce the worker. Preserve damaged bytes for investigation; verify an independent recovery set before considering restore |
| `unsupported` | Refuse unknown schema. Use a compatible binary or explicitly plan a verified migration after a complete backup; never edit `user_version` to bypass a gate |
| Processing runtime unavailable | Keep originals and queued jobs; check the optional local model separately. Never treat model unavailability as a reason to reset the catalog |

The automated scenarios verify fixed response categories, not a real full disk, hardware unmount, other-user access, power-loss durability or all OS/filesystem permission combinations. Permission-based negatives run only on Unix. The manual [coordinated recovery procedure](coordinated-recovery-set.md) and [security-registry recovery](security-registry-recovery.md) must be performed on the selected filesystem with operator-preserved evidence.

## Logs, retention, secrets and access

Worker operational logs are emitted to **stderr**, not written to a durable file by Praxis. Their UTC timestamp, per-run ID and sequence can correlate startup, shutdown and generic errors within a worker session, but no provider, user/source locator, original text or exception message is intentionally emitted. There is no implemented access-controlled log collector, metric exporter, retention job, multi-process trace, or alarm/alert pipeline. If an operator redirects stderr, their chosen collector must enforce owner-only access and a finite rotation/deletion policy; never redirect private stdout into it. Record the chosen retention, storage location and independent log-permissions test before claiming a supported managed deployment. No retention duration is currently approved.

The worker takes no transport credential and ignores application configuration files/environment for its service composition. That means **HTTP credential rotation is not applicable to this profile**, not that a rotation mechanism has been tested. Device private signing/wrapping keys, recovery factors, issuer CA material, integration refresh tokens and client trust pins require separate protected custody. Do not put them in the catalog path arguments, checked-in files, worker logs, screenshots or backup evidence. The [device issuer ADR](../decisions/0038-private-device-certificate-identity.md) outlines a future mTLS direction, but live certificate issuance, issuer rollover/revocation, TLS handshake and permission enforcement are **not implemented deployment evidence**.

## Update, rollback, capacity and cost boundaries

1. Quiesce the app/worker and verify exclusive catalog ownership is released. Preserve the exact previous binaries and SDK version.
2. Capture and independently verify a complete coordinated recovery set on fresh private paths. Preserve required client-held key material through its separate approved custody process; otherwise restoring the databases alone may not restore access.
3. Build/publish the replacement into a *different* directory; run the full disposable process smoke and the release gates in #46. Start the new worker only against the approved catalog after verifying the stored schema and explicit migration plan.
4. After update, verify the owner's library, sources, search and private app session. If the new binary fails before any migration, restore the previous verified binaries. If the schema or keys changed, do **not** simply run an older binary against them: stop and perform a separately validated restore/recovery with matching credentials and preserved evidence.

The configured worker has one catalog writer lease and bounded watched folders; the optional Kestrel health listener limits connections to 64. These are **configuration values**, not measured throughput or production sizing. Before choosing a service plan, record actual catalog/original bytes, working set, CPU, disk space, recovery size/time, monitored intake rate, log volume and expected model resource requirements on an approved workload. No cloud/provider, uptime objective, monthly cost, encrypted offsite backup policy, recovery-time objective or retention period is assumed.

## What remains before closing #41

This profile only verifies the local processes already authorized by the product's owner-local trust model. Completing the broader #41 definition requires a selected, *actually deployed* private-data environment with a verified network/OS trust boundary, TLS/mTLS or other approved authentication, live grant authorization and credential rotation/revocation, scoped secrets storage, denied-traffic tests, correlated and access-limited operational diagnostics, bounded log retention and measured resource limits. Backup, schema migration and rollback must be exercised on **the same selected environment**, with secret factors and client-held trust state, under #40/#46/#65. #107 still gates source-bearing local HTTP. The owner must explicitly approve any new host, spend, public ingress or key custody.

A green owner-local process smoke proves only that owner-local profile. Never use it to mark hosted or remote private routes as deployed or secure, or to close an unrelated security gate. Self-hosted Actions under [#137](https://github.com/berniemackie97/Praxis/issues/137) do not count as cross-platform verification until jobs actually run.
