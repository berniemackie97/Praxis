# Threat model and trust boundaries

**Status:** Security design baseline; deployment and data-processing mode pending [#50](https://github.com/berniemackie97/Praxis/issues/50).  
**Applies to:** Praxis server, Obsidian adapter, first-party clients, synchronization, ingestion, retrieval and authorized actions.  
**Scope:** Security requirements and verification obligations, not claims of implemented protection.

## Assets and adversaries

Assets include original notes and attachments, filenames and metadata, revision histories, deleted content, account/device identity, session credentials, encryption keys, backups, search indexes, excerpts sent to model providers, connector tokens, and action/audit records.

Consider a stolen or compromised device, an unauthorized network peer, an untrusted service operator or compromised host, a malicious connector or model provider, hostile retrieved documents and prompt injection, bugs in the client/plugin, and accidental operator mistakes. A compromised authorized endpoint may read information already decrypted by that endpoint; no transport or storage scheme eliminates that risk.

## Data flow and trust boundaries

| Boundary | Untrusted input or exposure | Required controls before enabling the data flow |
| --- | --- | --- |
| Local notes / Obsidian plugin | Files, symlinks, renamed paths, malicious attachments, plugin permissions | Treat vault contents as untrusted; constrain paths and file sizes; do not assume all desktop APIs exist on mobile; protect local pending changes |
| Client / operating system | Process memory, local cache, credential storage, malware, device theft | OS-backed secret storage when supported; lock-screen and disk security are external prerequisites; no persistent credentials in note files, app logs or exported content |
| Device / network / API | Replayed, altered, oversized, duplicated or unauthorized requests | Authenticated requests, TLS for non-loopback access, scoped authorization, strict parsing and size/rate bounds, replay-safe change semantics |
| API / persistent storage | Plaintext content, keys, revisions, backups, metadata exposure | Select one explicit processing mode; least privilege, encryption at rest, segregated secrets, retention controls, recoverable and tested backups |
| Content / indexing pipeline | Malicious documents, parser exploits, decompression bombs, poisoned context | Ingestion limits and isolation, provenance, permission-aware derived indexes, cancellation/resource bounds, no arbitrary local file or network access |
| Retrieval / inference provider | Private snippets, prompts, user identifiers and retained provider data | No outbound content without explicit user authorization and scoped disclosure; exclude credentials by policy; vendor retention and deletion assessed |
| Models / action tools | Prompt injection, confused-deputy attacks, dangerous tool parameters | Treat model output as untrusted; fixed tool permissions, scoped approvals, independently enforced authorization, audit, idempotency and outbound destination controls |
| Deployment / backups / CI | Exposed endpoints, secrets, artifact leakage, stale keys, damaged backups | Secure defaults, supply-chain review, backup encryption and key recovery, restore rehearsal, no production content in CI or logs |

A local-first interface is **not** an encryption guarantee. HTTPS and encryption at rest are **not** end-to-end encryption. Server-side indexing or inference over plaintext means the server can access that plaintext at processing time.

## Processing-mode decision

Two supported architectural modes are feasible, but neither should be claimed implemented until validated.

| Mode | Server visibility | Retrieval and automation | Primary tradeoff |
| --- | --- | --- | --- |
| Trusted processing service | Authorized service can read plaintext; transport and stored media are protected separately | Server may index permitted collections and run authorized workflows | Host compromise or operator access may reveal plaintext; not end-to-end encrypted |
| Client-held encrypted collection | Service stores opaque ciphertext; keys remain on authorized clients | Search, embedding and inference on clients, unless the user explicitly exports/decrypts selected content | Recovery, multiple-device key distribution and server-side intelligence become harder |

A collection-scoped hybrid may be considered later; it requires distinct processing states, key ownership and testable prevention of cross-mode leakage. Do not introduce an unimplemented encryption-mode flag or imply that a future mode already works.

### Effective policy until a mode is selected

1. Local file editing is permitted; **no remote storage of private content or public sync endpoint is authorized yet**.
2. No private note, attachment or excerpt may be sent to an external inference provider by default.
3. No connector receives third-party credentials or sync data until separately authorized.
4. Agent/tool actions are read-only by default; all mutations and external side effects require independently enforced scope and approval.
5. The current unauthenticated `/health/live` endpoint is liveness-only. It must never disclose collections, storage state, user identity, secrets or readiness details.
6. A service reachable beyond loopback needs an approved host/network boundary and authentication design first; an encrypted transport alone is insufficient.

This default is intentionally restrictive so missing configuration cannot silently enable sharing. [#50](https://github.com/berniemackie97/Praxis/issues/50) must resolve hosting, privacy, data-processing and recovery preferences before enabling remote synchronization.

## Identity, credentials and key custody

- Prefer a proven OpenID Connect identity service rather than implementing a password or OAuth authorization server in Praxis. For native clients, use browser-based authorization code flow with PKCE when OAuth is selected. Embedded client secrets are not secrets.
- Public clients must use refresh-token rotation or sender-constrained refresh tokens, as applicable; avoid long-lived bearer tokens in vault files, URL parameters, logs or browser-accessible storage.
- Enrollment creates an explicit device identity and authorization scope. Revocation denies future server access; it cannot erase copies already downloaded to a disconnected device.
- If client-held encryption is selected, clients own encryption keys; the server must not receive decrypting keys. Define enrollment, recovery material, rotation, lost-device handling, and loss-of-all-keys behavior before implementation.
- If trusted server processing is selected, document where decrypting keys are held, who can access them, how they are rotated and restored, and what backups contain. Disk encryption alone does not restrict the running service from reading records.
- Never invent application-specific cryptographic algorithms or treat key rotation as guaranteed secure deletion from all historical backups.

## Threat register

Priority indicates what must be blocked before private remote data is accepted; it is not a quantitative risk score.

| ID | Priority | Threat | Required mitigation / test owner |
| --- | --- | --- | --- |
| T01 | Critical | Unauthenticated or cross-account read/write of notes, attachments or change streams | Deny by default; per-record authorization at every API and derived index. Negative access tests in [#15](https://github.com/berniemackie97/Praxis/issues/15), [#19](https://github.com/berniemackie97/Praxis/issues/19) |
| T02 | Critical | Unapproved plaintext exposure through hosting, indexing or model calls | Explicit mode and per-source disclosure; redaction and no outbound request on deny. [#50](https://github.com/berniemackie97/Praxis/issues/50), [#31](https://github.com/berniemackie97/Praxis/issues/31), [#32](https://github.com/berniemackie97/Praxis/issues/32) |
| T03 | Critical | Replayed, stale or concurrent sync changes silently overwrite or resurrect content | Durable causal/version contract; reject or surface conflicts; adversarial tests. [#17](https://github.com/berniemackie97/Praxis/issues/17), [#21](https://github.com/berniemackie97/Praxis/issues/21) |
| T04 | High | Stolen/revoked device or session remains able to call server APIs | Enrollment, short sessions, rotation/revocation and server-side checks; offline possession limitation documented. [#15](https://github.com/berniemackie97/Praxis/issues/15) |
| T05 | High | Credential or sensitive content leaks through diagnostics, exceptions or backups | Structured metadata-only logs, secret redaction, secure storage, backup encryption, restore tests. [#40](https://github.com/berniemackie97/Praxis/issues/40), [#41](https://github.com/berniemackie97/Praxis/issues/41) |
| T06 | High | Malicious paths, symlinks, URLs or archive payloads traverse local/server boundaries | Canonical path/URI checks; no unrestricted fetch; size/decompression limits and parsing tests. [#22](https://github.com/berniemackie97/Praxis/issues/22), [#27](https://github.com/berniemackie97/Praxis/issues/27) |
| T07 | High | Prompt injection from notes, web pages or connector content causes unsafe action/exfiltration | Untrusted-content boundary, policy enforcement outside models, explicit approval and deny tests. [#32](https://github.com/berniemackie97/Praxis/issues/32) |
| T08 | High | Compromised connector grants broader access or retains data after revocation | Separate source credentials, minimal scopes, isolated source ownership, revocation and deletion tests. [#30](https://github.com/berniemackie97/Praxis/issues/30), [#35](https://github.com/berniemackie97/Praxis/issues/35) |
| T09 | High | Recovery fails due to key loss, stale backups, inconsistent attachments or revisions | Explicit custody/recovery policy, encrypted backups, verified whole-collection restore. [#16](https://github.com/berniemackie97/Praxis/issues/16), [#40](https://github.com/berniemackie97/Praxis/issues/40) |
| T10 | High | Unbounded uploads, processing or retries exhaust disk, memory, network or model budget | Resource limits, quotas, cancellation, bounded work/retries, cost ceilings, fault tests. [#19](https://github.com/berniemackie97/Praxis/issues/19), [#27](https://github.com/berniemackie97/Praxis/issues/27), [#47](https://github.com/berniemackie97/Praxis/issues/47) |
| T11 | High | Older clients or rollback corrupt current records | Version negotiation, migration compatibility, immutable originals and rollback fixtures. [#14](https://github.com/berniemackie97/Praxis/issues/14), [#17](https://github.com/berniemackie97/Praxis/issues/17), [#46](https://github.com/berniemackie97/Praxis/issues/46) |
| T12 | High | Unauthorized content is visible in cross-collection search results or derived caches | Authorization at query, snippet and cache boundaries; index cleanup/rebuild after revocation and deletion. [#28](https://github.com/berniemackie97/Praxis/issues/28), [#29](https://github.com/berniemackie97/Praxis/issues/29) |
| T13 | High | Human error, malicious update or lost device creates unrecoverable destruction | Audit, immutable/versioned restore points, tested revoke and restore operations. [#40](https://github.com/berniemackie97/Praxis/issues/40), [#46](https://github.com/berniemackie97/Praxis/issues/46) |
| T14 | Medium | Encrypted collections still expose timing, sizes, endpoints or device associations | Minimize metadata and log retention; document residual leakage. [#13](https://github.com/berniemackie97/Praxis/issues/13), [#50](https://github.com/berniemackie97/Praxis/issues/50) |

## Additional local capture, browser, voice and agent boundaries (planned)

These are requirements for future work, **not** evidence of implemented security controls:

- **Owner-local library:** accepting a directory or file means permission for the chosen source only, not for a whole home directory. The local service cannot assume that loopback implies authorization; browser origin bypass, DNS rebinding, alternate local users and token disclosure require explicit negative tests (#99/#100/#107).
- **Hostile input:** malformed PDFs/media/archives, code, game binaries, and fetched pages are untrusted. Never execute imported code or extraction instructions; bound file size, parser runtime, recursion and derived output, and do not silently upload content (#102/#103/#114).
- **Dictation:** microphone permission and active recording state are explicit; cancellation and retention must be tested. A transcript is a derived output with separate processing consent; no remote speech model receives audio by default (#109/#110).
- **Extension:** page scripts are untrusted, even on a tab the user chose. Paired browser capture needs narrow grants, cross-origin checks, content sanitization, an explicit queue and revocation, with no passive browsing-history surveillance (#111/#112).
- **MCP:** agent identities have separately scoped read and write capabilities. Retrieved notes/web pages cannot instruct the service to escalate permissions; tools require bounded arguments/responses and auditable approvals; no agent receives arbitrary disk access (#117/#118/#119/#32).
- **Deletion and index cleanup:** source revocation/deletion must retract unauthorized derived snippets, suggestions, cached responses and agent-visible results, even when identical bytes remain for another authorized source (#102/#106/#115/#118).

A local first-party demonstration may run without remote trust decisions, but enabling external access, third-party inference or unattended agent side effects still requires #13/#15/#50 and corresponding authorization evidence.

## Verified original-file publication boundary (S1b)

S1b issue #124 adds an explicitly invoked .NET stream-to-storage primitive. Only the caller supplies source bytes and absolute locator; no automatic source opening, watcher, browser permission, microphone access or remote transfer is added. The locator is **unverified provenance text** until the planned input-selection layer (#101/#102) validates that the stream came from a user-approved source.

The owner-selected data root is checked before capture. Staged originals are placed under private subdirectories, size-limited, hashed and flushed; a non-overwriting same-volume rename publishes them by lowercase SHA-256. Existing objects are rehashed before sharing. The catalog transaction comes after successful published-byte verification. A failed SQL transaction can leave an orphan verified object, and no automatic garbage collector is authorized yet. Retries and source-version updates are replay-safe for committed v2 receipts after S1c #126; the caller still supplies unverified source provenance.

Local filesystem and owner permissions are not a security boundary against another process with the same OS-user authority; the current implementation does not guarantee resistance to adversarial path substitution, hard power loss, Windows ACL misconfiguration or disconnected external media. A hash confirms bytes, not authenticity or access rights. The catalog, originals and derived content are **plaintext** until separately authorized encryption decisions. These limits remain in #50/#40/#107. The write primitive must not be exposed to browser pages or MCP agents without separate caller authentication and scopes.

## Explicit request receipts and interruption inspection (S1c)

S1c introduces caller-provided request keys and stable source/item revision history. A committed key returns the stored historical result **without re-reading the caller's stream**. This makes intentional retries repeatable, but does not authenticate source locators, prove a new stream matches the old stream or authorize a new actor to replay someone else's key. Future localhost, browser and MCP interfaces must bind request keys to an authenticated principal, source scope and expiration/revocation policy before exposing this API.

`InspectRecoveryAsync` reads catalog and object metadata and rehashes referenced original files. It can identify incomplete staging, orphaned publication, corrupt and missing references; it makes **no changes**. The scan is not a consistent snapshot across concurrent imports, and an orphan may still be in use. Retention policies, directory replacement risks, interrupted power/drive removal and complete media+metadata backups remain required under #100/#40. Do not expose source paths, file hashes, private content or inventory output to agents without #107/#117 access decisions.

## Coherent local snapshot trust boundary (S1d, PR #134)

The snapshot library uses SQLite's backup API for a committed catalog view, copies catalog-recorded originals into a private sibling staging directory, verifies the manifest and hashes, and refuses to publish or restore into existing final paths. A catalog digest is an integrity check, not a signature; a malicious same-user process able to replace the catalog and manifest can forge a self-consistent backup. The bytes and recovery metadata are **plaintext**. Owner-private Unix mode checks do not establish Windows ACL correctness.

Failures or interruption can leave \`.incomplete-...\` private directories. These are never completed backups and must not be automatically restored or removed without examining the last good copy. The backup excludes uncommitted staging and unknown object orphans, while retaining all committed source revision/receipt history. Restored incomplete request records stay inert; #102 must define future job recovery.

This PR provides library-level deterministic verification, not evidence of multi-GiB media, disk exhaustion, actual unexpected unmount/power loss, durable directory metadata on every filesystem or safe off-device encrypted recovery. Those remain #128/#40 and manual owner acceptance. Do not expose snapshot paths or destructive restore entry points to browsers/MCP without #107/#117 permission boundaries.

## Verification gates

**Before exposing a private-data sync endpoint:**

- [ ] Trust mode and key custody are documented and agreed (#50).
- [ ] Device enrollment, authorization, token revocation and negative cross-identity tests pass (#15).
- [ ] TLS/non-loopback deployment boundary and secret storage are verified (#41).
- [ ] Content and attachment limits, validation and replay behavior pass adversarial tests (#19, #21).
- [ ] An export, encrypted backup and recovery procedure exist with a successful restore test (#14, #16, #40).
- [ ] Logs and error responses are tested against sensitive-content leakage.

**Before permitting indexing, remote inference or action tools:**

- [ ] Processing consent and provenance are enforced by collection/source (#27, #28, #31).
- [ ] No unauthorized content leaves the designated trust domain (#31, #32).
- [ ] Prompt injection and cross-scope access tests demonstrate policy independent of model responses (#32).
- [ ] Action authorization, auditing and idempotent recovery are verified (#32, #34).

Failures in these gates block the feature, not just its release announcement.

## Security reference baseline

- [OWASP ASVS 5.0.0](https://asvs.owasp.org/) — application controls and testable access boundaries.
- [NIST SP 800-63B-4](https://csrc.nist.gov/pubs/sp/800/63/b/4/final) — authentication and authenticator management.
- [OAuth 2.0 Security BCP, RFC 9700](https://www.rfc-editor.org/rfc/rfc9700.html) — PKCE, least privilege and refresh-token replay mitigations.
- [OAuth 2.0 for Native Apps, RFC 8252](https://www.rfc-editor.org/rfc/rfc8252.html) — browser-mediated authorization for native/public clients.
- [OWASP Logging Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet) — exclude secrets and private content from logs.

These are design and verification sources; they do not certify Praxis as compliant. Review applicable requirements per shipped feature instead of claiming blanket adherence.
