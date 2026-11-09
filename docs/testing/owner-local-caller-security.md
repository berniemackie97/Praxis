# Owner-local caller security verification (#129)

This is the executable negative-test contract for [ADR 0021](../decisions/0021-owner-local-caller-security.md). It separates **what can be tested today** from future protected operations; a green present-day liveness test is not evidence that a non-existent authentication or IPC layer works.

## Baseline — executable now

The audited `Praxis.Api/Program.cs` exposes only `GET /health/live`. It has no private-data routes, caller identity, extension pairing or MCP interface.

```bash
dotnet restore Praxis.slnx --locked-mode
dotnet test tests/Praxis.Api.Tests/Praxis.Api.Tests.csproj -c Release --no-restore
dotnet test Praxis.slnx -c Release --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
```

`tests/Praxis.Api.Tests/LocalCallerBoundaryTests.cs` exercises the following with `WebApplicationFactory<Program>`, **without a TCP socket**:

| ID | Setup and request | Required current result | What it proves |
| --- | --- | --- | --- |
| B01 | `GET /health/live` | `200`, only the fixed liveness response, no source/identity detail | Liveness has no private content |
| B02 | `GET /v1/library`, `GET /v1/sources/00000000-0000-0000-0000-000000000001`, `GET /mcp` | `404`; no source body or permissive CORS header | No private reads are registered |
| B03 | `POST /v1/captures`, `POST /v1/sources`, `POST /mcp` with small harmless payload | `404`; no private operation exists | No private writes/tools are registered |
| B04 | `OPTIONS /v1/captures` with `Origin: https://attacker.example` and `Access-Control-Request-Method: POST` | `404`; no `Access-Control-Allow-Origin` | No cross-origin permission has been installed |
| B05 | Repeat a sample nonexistent private route with spoofed `Host`, `Origin` and fake bearer credential | Not successful; no source disclosure | Not an authentication test; route absent |

These are **absence-of-implementation checks**. They do **not** assert that the existing server rejects invalid Host headers, binds only to loopback, validates credentials, or secures stored files. Those must be exercised after #107, #112 and #117 implement the behavior.

## Required future fixture

Before enabling any source-bearing HTTP route, build a test fixture with a private temporary catalog containing:

- Active source A, independent active source B with **identical original bytes** but distinct source IDs, and a revoked source C with a saved derived record. Record original digests, metadata, processing jobs, and a pre-test database snapshot.
- An authorized first-party application context; separate integrations X (future paired extension with `capture` only), Y (future MCP `list/search/retrieve` for source A only), Z (unpaired); an expired X token, rotated and revoked X token, a token belonging to another runtime, and unknown/wrong-format tokens. Never put fixture credentials in logs or the repository.
- A controllable UTC clock; deterministic token/refresh exchange, recovery and revocation; two independent OS accounts for real OS-boundary checks; explicit IPv4 loopback and a nonloopback interface; a hostile browser origin and DNS-rebinding Host.
- For every denied operation, assert **zero change** to catalog, original object inventory, derivatives, grants and audit privilege state (apart from a bounded denial audit event). Compare body/headers of denied versus unknown source IDs to avoid resource enumeration.

The endpoint names below are descriptive **test fixtures** until #107/#112/#117 choose actual versioned routes. Future test implementations must bind to the real published routes; do not add fake endpoints simply to pass a test.

## #107 — listener, authentication and authorization (before any source endpoint)

| ID | Setup and request | Required denial, state and disclosure invariant |
| --- | --- | --- |
| L01 | Start host with a supported user-profile directory, then try `ASPNETCORE_URLS=http://0.0.0.0:PORT` and `--urls http://[::]:PORT` | Startup refuses wildcard/private-route exposure; verify the actual bound socket addresses with an external TCP probe, not just an in-process client |
| L02 | Explicit `127.0.0.1:0` listener, discover port from owner-private file | Discovered address is exactly loopback and actual assigned port; file is not a credential; stale/linked/disallowed discovery file fails closed |
| L03 | Send `Host: attacker.example:PORT`, `Host: 127.0.0.2:PORT`, a proxy `X-Forwarded-Host` and an unexpected IPv6 alias | `403`, no private lookup, no source data; rebinding or proxy header cannot substitute an allowlisted numeric host |
| L04 | `Origin: https://attacker.example` on GET and POST; `OPTIONS` preflight with attacker Origin | `403`, no Access-Control-Allow-Origin, no side effects. Identical denial when Origin is a browser extension before #112 enables pairing |
| L05 | Local `HttpClient` with **no Origin** and no credentials, then token in URL/query and cookie-only request | `401` for missing credential, query/cookie do not authenticate, and private data never returned. Localhost is not the grant |
| L06 | Missing/malformed/expired/rotated/revoked token, token for another runtime, invalid token audience | `401`; same generic safe errors; independent grant families unaffected, except deliberately revoked family |
| L07 | Valid token with no `read` grant attempts library search or original retrieval | `403`, no search hit/count or original-byte leakage |
| L08 | Valid source-A read token requests source B, revoked source C and unknown ID | Identical `404` for all unauthorized/unknown IDs; only source A is visible; same result in index/snippets/caches |
| L09 | Replay a refresh credential after a successful atomic renewal and after restart | Replay fails and its family is revoked; the other integration's token remains valid; no duplicated active renewal secret |
| L10 | Rotate/revoke one integration during an active read or capture | Subsequent operations fail immediately, in-flight commit is fenced, cached responses are not served with the revoked grant |
| L11 | Client sends `Content-Length` or chunked body exceeding per-route cap, then malformed JSON | `413` or a bounded `400`; no partially accepted source, no speculative metadata or memory growth outside enforced ceilings |
| L12 | Block worker, exceed deadline or concurrency quota, cancel halfway through an upload | Deterministic deadline/429 outcome, bounded active work and resource release; no falsely successful receipt |
| L13 | Compare denied vs missing-resource responses, log records and stored audit events | No paths, content, tokens, refresh handles, private query strings, untrusted Host/Origin or existence hints; bounded denial event only |

For a real listener test, use a separately spawned process and ephemeral port, inspect TCP bindings and try connections through loopback and a nonloopback interface. A `WebApplicationFactory` `TestServer` does not establish the network boundary.

## #112 — future paired extension (not enabled initially)

| ID | Setup and request | Required denial and state invariant |
| --- | --- | --- |
| E01 | Install extension without pairing, simulate page JavaScript posting to loopback | No usable credential, no cross-origin allowance and no capture accepted |
| E02 | Explicitly pair profile P1; reuse P1 token from unrelated profile P2 or a hostile webpage | Denied; credential cannot appear in page JS, referrer, web-accessible resource, error page or URL |
| E03 | Paired X capture-only token calls list/search/retrieve/update/delete/grant-management | `403`; no source content, count or mutation; a successful capture response must not return existing private data |
| E04 | Capture oversized or malformed HTML/selection, repeat request key after disconnect, cancel/disable extension | Bounds, idempotent receipt, failure state and local queue purge verified; no extra source versions or unapproved history collection |
| E05 | Revoke/rotate X, restart browser/host and replay old credentials | Immediate refusal; independent extension Y still works within its grant |

## #117 — future MCP transport and read-only agent policy (not enabled initially)

| ID | Setup and request | Required denial and state invariant |
| --- | --- | --- |
| M01 | Unpaired MCP client invokes list/search/fetch; invalid Host/Origin in Streamable HTTP | Denied before tool execution; follow current MCP discovery and HTTP 401/403 requirements, with no unauthorized data |
| M02 | Agent Y scoped to A queries B/C, searches text matching both A and B, requests oversized result | Only A permitted, bound snippets/response, no B/C count or metadata leakage |
| M03 | Agent Y requests capture/update/delete or unknown write tool; text from an imported source asks it to escalate | No advertised/allowed write capability, no policy mutation, no indirect tool execution |
| M04 | Revoke Y, replay expired/rotated token, reconnect after host restart | Existing grants checked anew, no unapproved retrieval or stale cached response; authorization protocol follows supported MCP spec |

## OS isolation, persistence and recovery

| ID | Owner / platform | Required evidence |
| --- | --- | --- |
| O01 | macOS APFS: owner U1 and separate local user U2 | U2 cannot read catalog, originals, credential/discovery files, connect to protected IPC or obtain a valid integration credential; unsafe parent/symlink permissions refuse startup |
| O02 | Windows NTFS: separate SIDs U1/U2 | Inherited broad ACL is rejected; private file/pipe DACL and peer identity verified; U2 cannot impersonate U1 using a per-machine pipe or discovery record |
| O03 | Linux: separate UIDs and private runtime dir | Owner-only mode, safe Unix socket path, peer identity, no `/tmp` symlink/race bypass or accidental permission widening |
| O04 | Untrusted external volume / unsupported ACL or permission semantics | Fail closed on sensitive runtime, secrets and private store; no fallback to world-readable files |
| O05 | Crash/restart or rollback with active grant, rotated refresh handle and queued source | No credential resurrection, no unauthorized live lease, no loss of revocation; recovery reports a non-success state if it cannot verify permissions |

## Evidence and gating

The present tests B01–B05 are a small baseline proof only. All L/E/M/O tests remain **not executed** until their owning implementation exists. #129 can close after owner-approved ADR, documented test plan, actual baseline proof, CI and reviewed merge, but this does not waive any future #107/#112/#117 test.

The associated decision is **owner-local only**. #13/#50 still own remote-hosting, encryption, model-processing, multi-device identity and recovery approvals. No remote listener, browser pairing or agent tools are authorized.

For M01–M04, the #117 compatibility harness must additionally verify the selected MCP protocol era, required per-request metadata/headers, POST-only Streamable HTTP and OAuth resource/audience challenges against representative clients; do not reuse obsolete 2025 GET-stream expectations with the 2026-07-28 transport.

Relevant technical references: [Kestrel addresses/port 0](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0), [Kestrel Host validation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-10.0), [ASP.NET Core CORS](https://learn.microsoft.com/en-us/aspnet/core/security/cors?view=aspnetcore-10.0), [MCP Streamable HTTP 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http) and [MCP HTTP authorization 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization).
