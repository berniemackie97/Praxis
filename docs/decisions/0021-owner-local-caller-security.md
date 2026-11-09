# ADR 0021: Owner-local caller and integration security

**Status:** Proposed for #129. Six product decisions approved November 8, 2026; engineering contract pending PR review and CI. This ADR does not authorize enabling an integration.

## Scope and evidence

Praxis's first-party application will serve one logged-in OS owner using owner-private local data. At the audited baseline, `src/Praxis.Api/Program.cs` registers only `/health/live`; it has no private-data route, authentication middleware, paired integration, IPC endpoint or user interface. `Praxis.Storage` offers an explicitly called plaintext local catalog with immutable originals and processing results; its caller-supplied paths, source IDs and idempotency keys **are not identities or authorization grants**. Therefore existing tests can establish that the current API has no private routes, not that future identity, network or OS isolation is implemented.

The selected design avoids creating an HTTP authority for the first-party UI merely to support later extensions or agents. Security work on future interfaces must not silently change this default. Remote access, user sharing, external inference, encryption mode, key recovery and server hosting remain gated by #13/#50.

## Approved product decisions (binding)

1. **Hybrid application:** first-party UI calls trusted application services **in-process** or through protected OS IPC; optional external integrations use a separate authenticated **loopback-only** interface. External callers never inherit the UI's authority.
2. **No browser access initially:** ordinary websites, browser extensions, embedded web content, CORS, cookies and privileged browser bridges are not routes into private APIs. The listener for external source operations remains disabled until separately authorized.
3. **Future extension capture-only:** explicitly paired per installation. May submit authorized new captures, not list/search/retrieve/modify/delete, manage grants or access other browser profiles. Page JavaScript never receives credentials.
4. **Future MCP read-only:** independently authorized per agent, source-scoped search/list/retrieve only. No capture, write or deletion tools. MCP transport, SDK compatibility and authorization standards remain owned by #117.
5. **Renewable limited credentials:** per integration, short-lived (15 minutes as the initial access-token ceiling), individually scoped, renewable, rotated and immediately revocable. No indefinite access credentials, secrets in URLs or logs, or cross-integration escalation.
6. **Per-OS-user isolation:** a separate private process and store per logged-in user. Never use loopback, an advisory lock or ownership of a request key as evidence of user identity. No household or multi-account sharing.

## First-party execution and OS trust

**Initial first-party choice:** the application host, owner-local catalog and trusted application services execute **in one OS-user process**; the UI calls them directly through application interfaces. The desktop UI framework/packaging decision stays under #36, but the chosen UI must be capable of this in-process service boundary. If that UI stack instead requires a renderer or helper process, it cannot receive an automatic privileged bridge: a new reviewed OS IPC contract is required before enabling it. No owner content is exposed to an ordinary website or a general-purpose browser view.

**Protected IPC, only if later needed:** on macOS/Linux, place Unix-domain sockets under a verified owner-only directory (`0700`) and independently validate peer identity/permissions; on Windows, use a named pipe with an explicit current-user SID ACL and verified peer identity. Resolve the per-OS peer-check API and exercise real process impersonation tests before shipping, rather than assuming socket-path permissions or a file lock alone authenticate the peer. A service in a different OS session is not a trusted UI process. A shared service account or privileged system daemon is not part of the initial release.

**Storage:** local catalog, originals, derivatives, grants, discovery metadata and backups must use an owner-private root. POSIX `0700` directories/`0600` files and absence of symlink traversal are necessary checks, not complete protection from same-user malware. On Windows, verify the effective ACL excludes other ordinary users rather than accepting inherited broad access. On removable/unsupported filesystems, refuse sensitive runtime and credential placement if ownership/security semantics cannot be verified; do not silently weaken checks. Validate parents, link/reparse traversal and changes after verification where applicable. macOS APFS, Windows NTFS and Linux owner-private roots require negative tests with separate OS accounts; filesystem-specific behavior remains unverified in the present spike. Do not place pairing secrets on an unverified external volume.

## Separately enabled owner-local HTTP interface

**Initial state:** no source-bearing routes or integration credentials; `/health/live` remains content-free. The present generic web host can be configured via host settings, so no claim is made that its existing liveness-only listener rejects all nonloopback configurations. Before #107 adds any private endpoint, it must enforce the following:

- Bind **only** the literal IPv4 loopback address `127.0.0.1` with a selected port. With Kestrel, `Listen(IPAddress.Loopback, 0)` supports an OS-assigned port; **`ListenLocalhost(0)` does not**. Explicit IPv6 `::1` support needs its own validated binding; no wildcard, LAN, reverse-proxy, named-host or fallback binding. Ignore/reject `ASPNETCORE_URLS`, `--urls` and other configuration capable of broadening the sensitive listener. Fail startup if binding or address inspection fails. Separate liveness-only configuration must not be reused to bypass this check.
- Discover the assigned port via an atomically published, owner-private file with strict permission, lifetime and process validation. The file locates a service; it is **not** a bearer credential or permission grant. Remove stale discovery records and prove an unrelated process cannot impersonate an authorized service without the independently verified caller credential.
- On **every** request, before route execution, reject a `Host` other than the exact configured numeric loopback host and actual port. Reject proxy/forwarded-host shortcuts and DNS-rebinding aliases. Host validation is independent of socket binding: Kestrel does not generally enforce arbitrary request Host headers.
- Deny `Origin`-bearing requests in the initial external policy, including `OPTIONS` preflight. No wildcard CORS, cookies, cross-site bearer bootstrap, CSRF-exempt state-changing routes or automatic redirect to a credential exchange. Absence of `Origin` does **not** authenticate a CLI/local process. Future extension origins require a separately reviewed #112 policy and paired identity; ordinary web origins remain denied.
- Authenticate all source-bearing operations using explicitly issued integration credentials; then authorize the operation and **every returned source** against a source-scoped grant, including counts, snippets, search results, caches and originals. Deny-by-default before parsing any private payload. Each route must require a precise capability; possession of a valid token never implies another operation or source scope.
- Do not expose a private route when credential verification, store protection, audit recording or resource limits cannot be enforced. No unauthenticated local diagnostic endpoints may return private state.

The initial route catalog is only `GET /health/live`, with no private source operations. Future route names in the test plan are **fixtures**, not published endpoints.

## Credential and grant lifecycle (future integration contract)

**First-party UI:** no HTTP token bootstrap or refresh grant. Trusted in-process calls use current OS-session ownership and application-level confirmation for destructive operations.

**External grant model:** grant ID, independent integration/install ID, kind (`extension` or `mcp`), allowed operation set, explicit source-ID allowlist (or a separately approved capture-only destination), issued/expiry/revoked timestamps and credential-family/version. Grants are not inferred from source paths, identical digests, browser tabs, MCP prompts or caller-provided metadata. The authoritative grant registry is owner-private and consulted on every protected operation.

**Access credential:** opaque CSPRNG token with at least 256 bits of entropy, audience-bound to one local Praxis runtime and one integration grant. Its 15-minute maximum lifetime is checked against server time, along with token version, source grants and immediate server-side revocation. Persist only a cryptographic token verifier, not reusable plaintext, in the owner-private grant registry; compare verifiers using a timing-safe operation. The exact issuance transport and wire format must be reviewed against #107/#112/#117 interoperability before implementation. Rotated/revoked access tokens are rejected even before natural expiry.

**Renewable pairing authorization:** a distinct high-entropy, per-installation secret stored in the OS-protected credential facility (macOS Keychain, Windows per-user credential protection, or an appropriately secured Linux secret service); the registry stores a nonreversible verifier. The owner explicitly pairs and approves capabilities using the first-party UI. A successful renewal atomically rotates the refresh secret and invalidates its predecessor. A replayed, already consumed renewal secret revokes that credential family and requires fresh owner pairing. Do not store renewal secrets in ordinary local files, browser page storage or logs. If a needed secure store/permission boundary cannot be validated, **disable the integration** rather than fall back to plaintext secrets. Extension storage and host-assisted renewal feasibility are separate #112 acceptance requirements, not assumed available here.

**Bearer limitation:** a stolen unexpired bearer credential may still be replayed **within its own scope** until revoked or expired. Short TTL, audience isolation, host/origin validation, renewal replay detection, idempotent captures and explicit revocation narrow this window; they are not a same-user-malware sandbox. No custom cryptographic algorithm, home-grown OAuth replacement or indefinite refresh token is approved. For HTTP-based MCP, #117 must conform to the applicable MCP authorization standard and select a compatible server rather than copying this provisional opaque-token exchange into an incompatible protocol.

## Permission matrix

| Operation | First-party trusted UI | Future paired extension | Future authorized MCP agent | Ordinary website |
| --- | --- | --- | --- | --- |
| Search/list library | Permitted | Denied | Source-scoped read-only | Denied |
| Retrieve original/derived text | Permitted | Denied | Source-scoped read-only | Denied |
| Submit a new source | Permitted after owner selection | Capture-only grant | Denied | Denied |
| Modify/delete source or derived data | Application confirmation and policies | Denied | Denied | Denied |
| Pair, rotate or revoke integrations | Owner UI only | Denied | Denied | Denied |
| Use a remotely reachable service | Not approved | Not approved | Not approved | Not approved |

Neither an extension nor an MCP endpoint is enabled by this ADR. An extension must not be allowed to read back newly submitted private content merely because the capture succeeded.

## Errors, quotas, and audit contract

Security checks occur before any private lookup or mutation. Host or forbidden Origin: `403` with generic body; missing/malformed/expired/revoked credential: `401` with a generic error; valid credential lacking operation scope: `403`; unknown **or unauthorized** source ID: identical `404` responses to avoid existence disclosure. No redirect, stack trace, token echo, user ID, source path or debug details on denial. Future HTTP MCP challenges must additionally satisfy its standards-mandated `WWW-Authenticate` behavior (#117). An excessive body returns `413`, a bounded concurrency/rate refusal returns `429`, and exceeded deadlines cancel work and avoid committing a false success. Failure must not leave an authorized partial mutation or leaked response.

**Proposed starting upper bounds for #107 validation, not implemented runtime guarantees:** `16 KiB` aggregate request headers; `256 KiB` structured JSON; `16 MiB` streamed capture per request (subject to the tighter per-source catalog cap); `100` list/search results per page; `4 KiB` maximum text excerpt; `8` concurrent requests per grant and `30 s` operation deadline. The first-party app has separate bounded ingest budgets. Exact defaults require representative workload and cross-platform negative tests; larger originals should use later bounded streaming/range contracts, not unrestricted buffering. The secure default is no external source API until these are enforced.

Audit records contain UTC time, event type, opaque correlation/grant identifier, capability name, result and bounded enumerated denial reason; optionally a protected source identifier only when necessary for incident review. **Never** record content, locators, request bodies, credentials, refresh handles, full URL query strings or unsafe Host/Origin values. Store records in an owner-private store; initial retention target 30 days with owner-accessible deletion, size cap, and no network export. A failure to persist a required security event prevents the corresponding privilege grant/change; high-volume denial events use bounded counters without unbounded log growth. Values and actual retention enforcement are owned by the implementing issue.

## Alternatives considered

| Approach | Decision and consequences |
| --- | --- |
| Hybrid in-process UI plus separately authenticated loopback integrations | **Selected.** Keeps owner UI authority out of the network API; distinct lifecycle and threat models add some integration work. |
| One authenticated loopback HTTP API shared by UI and integrations | Rejected initially: would require granting a renderer/browser or unrelated local process a high-privilege token and securing an unnecessary first-party network hop. Could be viable with a well-designed trusted client in another product. |
| OS IPC exclusively for every caller | Rejected as the sole integration approach: strong local identity opportunities, but external extensions and heterogeneous MCP clients lack a universal portable OS IPC surface. |
| Ordinary browser-origin access | Not approved. CORS does not authenticate callers; hostile websites and rebinding must remain denied. |
| Broad MCP write tools | Not approved initially; independent #32 authorization/approval is required before any future expansion. |
| Indefinite bearer credentials | Rejected: difficult to revoke and high exposure on leakage; short-lived, renewable, isolated credentials chosen. |
| Privileged system daemon with a separate OS service identity | Not selected initially; adds installation, privilege and multi-session complexity to a one-owner local application. |

## Feasibility evidence and unresolved implementation validation

- The actual `Praxis.Api` host registers **only liveness**, so current integration denial can be exercised without introducing a token service. Added in-process endpoint tests under #129 show that representative private route fixtures have no handler; they cannot test socket binding, OS ownership or future authenticated routes.
- Microsoft Kestrel endpoint documentation confirms explicit IP binding and OS-assigned port `0`, but excludes `ListenLocalhost(0)`. Microsoft also documents that host prefixes and request `Host` validation are separate concerns. See [Kestrel endpoint configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0) and [Kestrel host filtering](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-10.0).
- Microsoft documents CORS as a browser relaxation mechanism, **not** API authentication: [ASP.NET Core CORS](https://learn.microsoft.com/en-us/aspnet/core/security/cors?view=aspnetcore-10.0).
- The [current MCP 2026-07-28 transport specification](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http) uses stateless request-scoped Streamable HTTP POST with mandatory Origin verification; it no longer specifies the earlier GET stream and requires per-request version/capability metadata. [HTTP authorization](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization) specifies OAuth-compatible authorization-server discovery, audience binding and token enforcement. #117 must test the target clients' actual protocol-era compatibility, including clients using the older initialization/GET-stream behavior, before choosing a transport or shipping an MCP listener.
- No trusted UI runtime, OS IPC proof, per-user ACL proof, secure credential store, owner-only port-discovery channel or hostile-origin enforcement has yet been built. Those are **tests to perform before exposure**, not existing guarantees. The approved product choices do not authorize remote hosting or model processing.

## Implementation handoff and closure gates

- **#107:** select an owner-local UI package supporting in-process application calls; keep source HTTP routes absent until the authenticated loopback listener, startup bind checks, Host/Origin/CSRF denial, grant enforcement, bounded request handling, safe errors and audit tests pass. This issue must test real TCP binding, not merely `WebApplicationFactory`.
- **#112:** only if scheduled after #107, develop explicit per-install pairing, isolated capture-only credential storage/renewal, extension-vs-page separation and strict upload/consent limits. No search or retrieval grants.
- **#117:** choose an MCP-spec-compatible client/server transport and authorization mechanism, with read-only source-scoped access. Do not enable or advertise MCP tool endpoints before #106 and #107 gates pass.
- **#13/#50:** link the local decision for threat-model consistency but leave remote hosting, device sharing, encryption/key custody, backup and recovery owner decisions open.
- **#129:** close only when this ADR, a reproducible negative-test plan, representative baseline tests, cross-platform CI and owner review are all accepted and merged. No other security issue or board item becomes Done because this ADR exists.

See [executable security verification plan](../testing/owner-local-caller-security.md).
