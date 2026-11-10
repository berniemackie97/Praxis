# Remote trust and privacy-boundary verification contract (#13)

**Status:** Design and negative-test requirements. Not evidence that remote identity, encryption, sharing or synchronization is implemented.
**Scope:** [ADR 0003](../decisions/0003-processing-trust-boundaries.md), [threat model](../security/threat-model.md), closed product-decision #50; implementation owners #15/#18–#21/#32/#40/#41. Owner-local integration attacks have an independent [verification contract](owner-local-caller-security.md).

## Present executable baseline (do not overstate)

`Praxis.Api` currently maps only `/health/live`. `tests/Praxis.Api.Tests/LocalCallerBoundaryTests.cs` exercises liveness, representative unregistered read/write/MCP routes, hostile CORS preflight, and spoofed Host/Origin/token inputs through an **in-process** `WebApplicationFactory`. It proves only the current routes and current in-process behavior; it cannot show real TLS/host binding, authentication, per-device keys, operating-system peer verification or deployed network isolation. The macOS SwiftUI app uses a separate .NET child worker; inherited-pipe security, effective OS user and local filesystem permissions require independent real-process inspection (#215/#107).

Reproduce the existing baseline without depending on real owner content:

```sh
dotnet restore Praxis.slnx --locked-mode
dotnet test tests/Praxis.Api.Tests/Praxis.Api.Tests.csproj -c Release --no-restore
dotnet test Praxis.slnx -c Release --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
python3 -m unittest discover -s scripts/github -p 'test_*.py'
```

**No execution claim is made by this plan.** #137 currently blocks normal GitHub Actions runner validation. Once a runner is available, capture the commit, host/OS and executed test counts; for implementation gates, test against the actual service and two independently persisted clients, not just a protocol fixture.

## Deployment, processing and identity attack cases

The following IDs are permanent evidence handles. The test owner must record the implementation PR and actual execution environment before claiming a result. `DENY` means no private bytes, private existence information, side effect, durable receipt or derived output leaks through the failed action; it also implies bounded nonsecret diagnostic reporting.

| ID / owner | Adversarial condition | Required observable result / proof |
| --- | --- | --- |
| S01 / #15, #19 | A new install has local-only sources; request a remote sync, agent job, diagnostic dump or implicit fallback without a grant | **DENY**, zero remote content/metadata egress; a local user action cannot silently authorize host placement |
| S02 / #15, #19 | Authenticated session claims another principal, household member or security-space ID, or directly guesses a record, attachment, cursor or receipt ID | **DENY** at every read/write/search/list/count/event/conflict and cache boundary; same response shape for nonexistent and unauthorized resources |
| S03 / #15 | Unauthorized device copies `deviceId`, `operationId`, source ID or a scoped but different-device credential; replays enrollment/session proof | **DENY**; cryptographic enrollment and the principal/device/grant binding are independently verified |
| S04 / #15 | Expired, rotated, stolen or revoked token/device attempts refresh, pull, push, key retrieval or migration; network drops during rotation | **DENY** future service operations; replay recovery cannot reissue authority, and acknowledged legitimate changes remain durable |
| S05 / #15 | Owner explicitly shares a household security space, then revokes one recipient while another remains; removed device stays offline | Post-revocation reads/actions and delivery of **future** key epochs denied; other members unaffected; already downloaded plaintext cannot be retroactively erased |
| S06 / #15, #19 | Server attempts to decrypt, index or model-infer on client-held ciphertext or receives a malicious key-envelope replacement | No host-readable content key, plaintext or decryptable derived index; clients reject unauthenticated envelopes, revisions and ciphertext |
| S07 / #15, #21 | Untrusted opaque relay withholds, reorders or replays old valid encrypted versions or sends a false epoch/cursor | Authorized clients detect verifiable mismatches/rollback against durable local state, fail or explicitly require rebootstrap; no silent resurrection or lost pending writes |
| S08 / #15, #32 | Trusted-host grant authorizes retrieval for one security space but an agent/model tries a different source, destination, external provider or action scope | **DENY** even if prompt text asks otherwise; no implicit permission to send excerpts outside the host |
| S09 / #15, #19, #32 | A caller flips an in-place `mode` field, modifies plaintext/ciphertext class, or reuses an old signed grant after reclassification | **DENY**; explicit owner-authorized migration with new boundaries, versioned content movement and audited old derivatives required |
| S10 / #15, #40 | Owner loses one device, then the last enrolled device; uses independently held recovery material to add a new device | Wrong/stale recovery factor denied; valid recovery restores only collections the factor controls; no server/account-admin recovery bypass |
| S11 / #15, #40 | Client-held ciphertext is restored after *all* authorized keys and independent recovery material are lost | Report irrecoverable ciphertext; no false “restored plaintext” or hidden escrow claim; backup can still verify ciphertext integrity |
| S12 / #40, #41 | Host's encrypted backup is corrupted, stale, missing an object, missing necessary keys, or taken mid-rotation | Verify rejects invalid/incomplete restore; clean restore preserves all acknowledged versions, grants, key epoch and event receipts or explicitly reports RPO violation |
| S13 / #19, #40 | Sync update is acknowledged, host crashes, follower lags and power/media are removed while an offline client retains pending changes | Durable receipt not invented; retained edits survive; measured acknowledgment/RPO semantics and tested promotion/recovery match the chosen deployment contract |
| S14 / #19, #21 | Authenticated device replays valid operation or uses same key with different payload; conflicting delete/rename; stale cursor | Follow ADR 0006: original receipt on identical replay, conflicting reuse rejected, tombstone and pending edits retained, explicit snapshot/rebootstrap |
| S15 / #19, #41 | Host is exposed on LAN/public network by configuration error; hostile Host/Origin, reverse proxy, redirect, TLS downgrade or untrusted issuer | No unauthenticated private route; explicit approved ingress/TLS configuration, validated issuer/audience and bounded failure; local listener cannot be promoted by environment defaults |
| S16 / #15 | OAuth/OIDC native/browser authentication with swapped issuer, callback redirect, mismatched PKCE verifier, code reuse or token audience | Reject all invalid combinations; use audited compatible authorization-code + PKCE flow; no embedded permanent client secret or unsafe password grant |
| S17 / #28, #29, #32 | Two users have equal-content originals; one source revoked; shared search, embeddings, caches or a citation from an old grant persist | No results/counts/snippets/derived facts from the revoked source; the surviving permitted source retains independent provenance and access |
| S18 / #31, #32 | Local model outage or malformed output triggers proposed remote inference; adversarial note contains tool commands | No automatic external disclosure; explicit selected/redacted payload approval, injection treated as evidence only; tool mutation denied absent independently checked grant |
| S19 / #107, #215 | Another OS account, hostile same-user process, forged worker/discovery path or inherited handle attempts privileged owner-local access | Test actual app/worker/OS ownership and IPC boundary; reject cross-account access, document same-user-malware limit, no accidental private HTTP listener |
| S20 / #19, #27, #41, #47 | Large media, repeated retries, corrupted ciphertext, many concurrent principals, disk exhaustion or aggressive index requests | Enforce measurable upload/CPU/memory/disk/latency/cost quotas, bounded cancellation, isolation and safe restart; no unlimited resource promises at TB/PB scale |
| S21 / #15, #40, #46 | Export a plaintext archive from a protected collection, import into another class, downgrade client schema or restore older snapshot | Require explicit owner disclosure/target approval; preserve source IDs and version/integrity constraints; refuse unsafe mode/schema downgrade and leaked derived plaintext |
| S22 / #15, #32, #41 | Remote operator, log consumer or backup-reader lacks collection processing grant; compare denial errors and telemetry | Logs/audits do not contain content, locators, tokens, query payloads or raw model inputs; role/tenant isolation prevents data access except explicitly trusted-host exposure |
| S23 / #15, #28, #29 | User B inserts source from private security space A into a shared **organizational** collection; authorized member of that view reads counts, search, excerpts and identical-digest objects; remove and re-add membership | Organizational membership never grants authority, changes key class or replicates content. B only sees individually permitted sources; neither digest equality nor grouping reveals denied source existence. A source cannot silently have two security spaces; an approved cross-space copy has distinct provenance/identity |

## Coverage and required evidence by release boundary

- **#13 / security design exit:** approved three-class data/key-flow inventory; principal/device/agent authority matrix; threat register mapped to S01–S23 and implementing owners; documented limits on offline revocation, hostile host, metadata visibility and recovery key loss. Present baseline is factual; implementation tests are **planned**, not falsely reported executed. This exit must not depend on #15 having already implemented remote enrollment, because #15 is legitimately blocked by #13.
- **#15 / remote identity implementation:** real cryptographic device enrollment and account/principal binding, collection grants, key custody and recovery, S02–S07/S10–S11/S16/S22/S23 on more than one persisted client and process restart. For self-hosted deployments, tests must not require a paid vendor.
- **#18–#21 / sync transport and protocol:** S01/S07/S09/S13–S15/S20/S21, network interruption, two persistent replicas, collection isolation, bounded upload/attachment and coherent reset.
- **#31/#32/#117 / models and agent tools:** S08/S17/S18/S22/S23 on actual enabled integration scopes; no prompt/model text authority or unconsented remote disclosure.
- **#40/#41/#46 / recoverable host:** S10–S13/S15/S20–S22 on selected real topology, encrypted backup, independent recovery/key custody, measured restore time and verified end-to-end data integrity.
- **#107/#215 / owner-local and OS platform:** S19 real Mac app/worker and operating-system account/IPC checks; distinct Windows/Linux VM and physical iOS/Android testing when those clients are implemented. Do not claim mobile or desktop platform verification just because hardware is available.

## Test data, reporting and closure rules

Use **synthetic tenants** (owner A, household member B, outsider C), at least two independently enrolled devices, two security spaces with distinct content keys, two owner-authored organizational collections with overlapping source memberships, two identically hashed originals with distinct source scopes, an opaque encrypted space and a separate trusted-processor space. Add source revisions, tombstones, denied and allowed agent calls, encrypted snapshots and tampered/expired credentials. Never commit real user keys, account cookies, credentials, household content or recoverable user backups.

For each executed case record: commit, platform, runtime versions, client/server processes, host topology, principal/device/grant configuration, encryption class, exact failure injection, observed status/side effects, private-data leakage check, persisted receipts and log redaction. Report **Not Implemented**, **Not Run**, **Failed** and **Passed** distinctly. Not Implemented is a legitimate state before #15/#19; a static table is not evidence of passing a security test. A fake server or in-memory fixture cannot establish host trust or recovery promises.

Sources of normative security guidance: [NIST SP 800-63-4](https://csrc.nist.gov/pubs/sp/800/63/4/final), [OAuth 2.0 Security BCP (RFC 9700)](https://www.rfc-editor.org/rfc/rfc9700.html) and [OWASP ASVS 5.0.0](https://asvs.owasp.org/). Map only applicable requirements when building the real remote services; citing these documents does not make Praxis certified.
