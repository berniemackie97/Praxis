# Owner-local health-only API deployment

**Scope:** #41 deployment hardening, initial diagnostic surface. The sole published HTTP host exposes two fixed diagnostic strings on IPv4 loopback. This is not a remote Praxis installation, private-data API, browser bridge, TLS endpoint, MCP server, agent service or network synchronization service.

The SwiftUI first-party app continues to launch a private .NET worker over inherited local pipes, documented in [the macOS workbench](../delivery/macos-workbench.md), [owner-local worker lifecycle](owner-local-worker.md) and ADR 0021. The first-party application does not need the health listener.

## Manual operator deployment

Use the pinned .NET 10 SDK/runtime (`global.json`), an unoccupied local port, and an unprivileged owner-local OS account. No cloud provider, paid subscription, external key store or internet access is needed after dependencies are restored.

```sh
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet publish src/Praxis.Api/Praxis.Api.csproj -c Release \
  --no-build --no-restore -o artifacts/publish
python3 scripts/release/smoke_api.py
PRAXIS_HEALTH_PORT=5080 dotnet artifacts/publish/Praxis.Api.dll
```

From a second local terminal:

```sh
curl --fail --silent --show-error http://127.0.0.1:5080/health/live
curl --fail --silent --show-error http://127.0.0.1:5080/health/ready
```

The fixed results are `Healthy` and `Ready`. Readiness means **only** that the diagnostic process has finished startup and has not started shutdown. It checks **no** SQLite catalog, security registry, source processor, Ollama service, volume, private authentication or backup. Stop with Ctrl-C or SIGTERM and start it again to rehearse shutdown/restart. No daemon, service manager, installation account or automatic update mechanism is established by these commands.

## Listener and resource limits

The Kestrel listener binds **only** to IPv4 `127.0.0.1`, not a hostname, IPv6, wildcard or LAN adapter. The code discards Kestrel's automatically loaded endpoint configuration (including `Kestrel:Endpoints`) and defines exactly one endpoint; explicit endpoint configuration also overrides URL settings such as `ASPNETCORE_URLS` and `--urls`. Only `PRAXIS_HEALTH_PORT` selects the port; it must be decimal **1024–65535** and defaults to **5080**. Invalid values and an occupied port fail startup instead of selecting a fallback or widening network exposure.

Kestrel caps concurrent connections at 64 and keeps its keepalive and header-read timeouts to ten seconds. These are conservative initial bounds, **not measured production capacity**. The published-process smoke test deliberately supplies hostile wildcard settings through both `ASPNETCORE_URLS` and `Kestrel:Endpoints`, probes an alternate loopback IPv4 address, tests both health routes, checks that a private route is absent and tests invalid-port startup refusal. This is evidence for that running process, **not** all possible firewalls, interfaces, DNS-binding attacks or deployment configurations.

There is no private-data route, CORS grant, bearer token, cookie, database access, application credential or secret environment variable in this HTTP host. `/v1/library`, `/v1/sources`, `/v1/captures` and `/mcp` remain unregistered. HTTP liveness and loopback addressing **never authenticate a caller**. Actual private HTTP operations remain blocked on #107's caller identity, exact Host/Origin, grant, authorization, denial and real-process tests. Externally reachable services additionally require separately approved TLS/mTLS, ingress, privacy, identity and key custody (#19/#41); this slice authorizes none of them.

## Diagnostics, secrets and retention

Only fixed-content liveness/readiness responses are published. ASP.NET informational request-start and route-execution logs are suppressed so request paths and query strings are not normally printed. No source bodies, IDs, private file paths or secrets are intentionally passed to HTTP diagnostics. This is **not** a guarantee that all host/framework error logs are redacted. Never pass credentials in URLs, process arguments, checked-in runtime configuration or issue comments.

The selected operator/process supervisor must enforce owner-only log access, a bounded retention policy and error redaction on any future *data-bearing* service. This manual diagnostic process does not yet have a complete structured correlation/tracing, monitoring or credential-rotation system. The native worker has its own error/observability surface. Do not interpret this host's fixed health strings as a service-wide readiness signal.

## Upgrades, rollback and storage recovery

The health-only process has no durable state or migrations. Stop the process, build and smoke the replacement, start the replacement on the same local port, and verify both responses. If it fails, stop it and restart the previously verified published version. This does **not** roll back a knowledge catalog, security registry or client-held key epoch.

For storage, follow the [coordinated recovery runbook](coordinated-recovery-set.md) and [identity registry recovery](security-registry-recovery.md) on approved fresh paths. Production app/worker secrets, private key custody, secure deployment topology, service account privileges, backup encryption and retention, release signing, migration rollback, physical power-loss testing and RPO/RTO are not yet established. Their owners include #46, #65, #128, #215 and #137. This diagnostic-host procedure is a bounded implementation slice, **not complete #41 acceptance**.

## Verification

```sh
git rev-parse HEAD
dotnet restore Praxis.slnx --locked-mode &&
dotnet build Praxis.slnx -c Release --no-restore &&
dotnet test tests/Praxis.Api.Tests/Praxis.Api.Tests.csproj \
  -c Release --no-build --no-restore &&
dotnet test Praxis.slnx -c Release --no-build --no-restore &&
dotnet format Praxis.slnx --verify-no-changes --no-restore &&
dotnet publish src/Praxis.Api/Praxis.Api.csproj -c Release \
  --no-build --no-restore -o artifacts/publish &&
python3 scripts/release/smoke_api.py
```

Do not claim CI green until the GitHub Actions OS matrix actually executes. #137 remains unresolved.
