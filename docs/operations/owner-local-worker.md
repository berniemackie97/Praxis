# Owner-local worker lifecycle and diagnostic boundary

**Scope:** [#41](https://github.com/berniemackie97/Praxis/issues/41), following the [health-only deployment](health-only-deployment.md) and [owner-local deployment acceptance profile](owner-local-deployment-profile.md). The macOS application launches a .NET worker with inherited stdin/stdout pipes. This is **not** an authenticated HTTP deployment or a remote service. Private library responses travel over the parent's inherited pipe; stderr is for coarse operational status and must not carry source locators, provider names or exception messages.

## Deployment and process ownership

Use the pinned .NET 10 SDK/runtime, an unprivileged owner account and an owner-private existing catalog directory. Keep the watched Inbox outside the catalog. On macOS, the first-party application sets up the private root; direct `--stdio` startup refuses a missing catalog instead of silently initializing another store.

```sh
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
python3 scripts/release/smoke_worker_protocol.py
python3 scripts/release/smoke_worker_lifecycle.py
```

The worker can be started directly for a disposable owner-local test with `dotnet src/Praxis.Worker/bin/Release/net10.0/Praxis.Worker.dll --data-root <absolute-private-catalog> --intake <absolute-existing-inbox> --stdio`. The parent must own both pipes, enforce its own request/response limits and terminate the child when the session closes. Use only disposable data for manual fault injection; never run two worker instances on the same production catalog.

The catalog lease prevents a second writer from opening the same root. EOF on the desktop input pipe requests graceful shutdown of intake, processing and verification, then releases the catalog. A background service that ends unexpectedly is treated as a failure, not as an ordinary successful worker exit. The service group cancels its peers and awaits their completion before the catalog is disposed. Cancellation of in-flight processing may leave recoverable leases; reopen and reconcile through the documented startup path.

## Diagnostics and recovery

The serving worker runs on the .NET Generic Host with its defaults disabled, so no configuration is read from environment variables or files; services are composed in `src/Praxis.Worker/Composition`. Worker stderr contains UTC timestamps, a fresh per-worker run ID, a monotonic per-run event sequence and coarse start/stop/status messages, written by a formatter that never includes exception details, and process-level failures emit a fixed category such as `access_denied`, `directory_unavailable`, `io_error`, `storage_error`, `invalid_input`, `invalid_state` or `unexpected_failure`. The worker does **not** echo the failing exception's message or source paths to stderr. Detailed source-level intake errors remain in the private owner-local `intakeStatus` desktop response, which can include private paths. Do not persist that response as an unredacted diagnostic log.

If the catalog cannot be opened, stop the worker, check owner account and filesystem permissions, inspect the mount and schema compatibility, then restart with the same existing root. Do not delete lock files, auto-migrate an unsupported schema, substitute a new empty catalog or log the path/secret while diagnosing. For loss/corruption use the [coordinated recovery set](coordinated-recovery-set.md) and retain independent owner-held key material. Recovery subcommands are explicitly owner-invoked and emit structured reports on stdout; keep those reports private.

The first-party app currently owns worker lifetime; this procedure does not establish a service manager, automatic restart loop, credential store, log retention daemon, OS-level log ACL policy, backup encryption, release signing, exact cost envelope or secure private HTTP ingress. Any external supervisor must separately enforce owner-only log access, bounded retention and deliberate restart/backoff rather than repeatedly restarting against an offline or incompatible store. No credentials are passed through checked-in configuration, diagnostic URLs or the worker's argument list.

## Verification boundaries

The real-process smoke covers missing-catalog refusal, concurrent catalog lock rejection, sanitized stderr, correlated startup/shutdown logs, graceful pipe EOF and reopening the same catalog. It also checks unexpected catalog entries, Unix privacy permissions, symlink refusal, corrupted SQLite data and refusal to open a future schema without rewriting its version. The existing framing smoke verifies bounded requests and invalid UTF-8 rejection. Unit coverage for catalog migrations, snapshots and source reconciliation remains separate. Neither smoke emulates power loss, hard unmount, cross-platform permissions, credential rotation, an authenticated network service or full post-restore recovery of independently held device keys.

These extended checks are **pending execution** on this PR head until the owner runs them; queued self-hosted Linux/macOS/Windows Actions under [#137](https://github.com/berniemackie97/Praxis/issues/137) are not green. The previous 673-test run was completed on the predecessor code and must not be transposed to this branch. [#41](https://github.com/berniemackie97/Praxis/issues/41) and [#65](https://github.com/berniemackie97/Praxis/issues/65) remain open for full deployment, security, secrets and observability acceptance.
