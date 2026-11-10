# Threat model and trust boundaries

**Status:** Security design baseline and mode-specific threat contract for #13. Owner product choices are complete in [#50](https://github.com/berniemackie97/Praxis/issues/50); identity, key lifecycle, remote processing, recovery and deployment controls remain **unimplemented and unverified**.  
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

## Selected processing and placement boundaries — #50 / ADR 0003

Deployment and collection protection are different axes: the owner chooses fully offline/local, self-hosted reachable on expressly selected networks, or remote-hosted Praxis; **per-security-space policy** determines what may leave a device and what the receiving processor can decrypt. Owner choices in #50 establish **three** mandatory policy classes; the [security decision](../decisions/0003-processing-trust-boundaries.md) is the normative design, not proof of implemented controls.

| Collection class | Local client | Remote service / operator | Permitted inference and derived indexes |
| --- | --- | --- | --- |
| Local-only | Retains originals, key/OS-local authority, excerpts and derivatives | No private content or source metadata; absence of explicit placement grant means no upload | Owner-local authorized processing only; present data and snapshots are plaintext |
| Client-held encrypted sync | Keeps content-decrypting collection keys, validates ciphertext and indexes locally | Only opaque ciphertext and minimal visible routing/event metadata, subject to authenticated owner/device/collection scopes | Local authorized devices only; untrusted host cannot independently search plaintext or run private agents |
| Trusted-host processing | Explicitly approves particular host(s), collection keys and processor authority | Authorized host/operator can read plaintext and derived content; host trust and backup key custody matter | Host-local background agents only within collection and tool grants; external provider disclosure separately approved |

**Trust-flow boundaries:** local-only capture must never cross a network merely because a remote installation exists; ciphertext must never be decrypted by the opaque relay, and sharing of one authorized security space must never make unrelated household knowledge visible to everyone. Authorized local inference, trusted-host processing, and a separately approved external provider each cross different trust boundaries; consent for one is not consent for another. Denied membership or revoked source IDs cannot remain visible in search indexes, embeddings, excerpts, cached answers, exports or agent tool results.

**Transition rule:** changing any security space's plaintext/ciphertext authority requires an authenticated owner-approved migration, explicit destination and key handling, reindex/revocation of old derivatives, audit and awareness that historic plaintext disclosures and offline copies cannot be recalled. A mode field on a request cannot authorize a transition; no silent server-side downgrade or implicit backup upload. Missing/unknown mode/version fails closed.

**Security-space identity is not an organizer collection:** the existing `source_collections` tables (ADR 0023) are many-to-many user-authored views, not tenant, ACL, key or synchronization boundaries. Every source/revision has exactly one authoritative security space; explicitly copying into another space creates a distinct source identity and provenance link. A query over an organizational collection must filter every source and derived result by its own security-space grants. An identical physical SHA-256 object shared by different spaces cannot grant read rights or reveal that a foreign space contains a source. See [ADR 0003](../decisions/0003-processing-trust-boundaries.md) and S23 below.

**Metadata and malicious-relay limits:** ciphertext still reveals some traffic, account/device, size and timing metadata; a storage host can suppress responses or replay older authenticated objects. Client rollback/freshness checks require durable trusted state and explicit recovery/rebootstrap handling; encrypted bytes alone do not prevent a malicious host from withholding updates.

### Default policy while implementations are incomplete

1. Existing first-party local capture/search operates within its documented **plaintext owner-local** boundary; no remotely accessible private-data service is enabled by #50 or this threat model.
2. No private note, attachment, excerpt, model prompt or export is sent to an external provider by default; explicit locally approved subscription-CLI verification is bounded to its approved payload and does not grant generalized cloud inference.
3. Every remotely reachable private operation must have real authenticated principal + enrolled device + resource/scope grant, TLS and the selected security-space class enforced before route activation (#15/#19/#41). No unchecked host or tenant-supplied metadata creates authority.
4. Agent/tool actions remain denied unless independently approved and enforced by execution permissions (#32). Prompt text, retrieved source contents and model responses cannot grant capability.
5. `/health/live` is liveness-only. Its present in-process negative tests do not prove actual network binding or future authorization.

The completed [#50](https://github.com/berniemackie97/Praxis/issues/50) resolves the *product policy*, not the security implementation or tested operational topology. See the [remote boundary negative-test matrix](../testing/remote-trust-security.md) for exact evidence owners.

## Identity, credentials and key custody

- **Principal vs device vs agent:** a remote account identifies a person; an enrolled device proves possession of its own authentication key; agents and integrations use **separate scoped caller identities**. Require authenticated issuer, session audience, security-space membership, resource/action scopes and revocation checked on every protected request. Source, security-space, organizational collection and replay IDs are not credentials. Use a no-mandatory-paid-provider standards-based authentication path; OAuth/OIDC public clients use authorization-code/PKCE with proper issuer/audience checks and token replay defenses (RFC 9700). Exact provider/device proof details, token/revocation tests and OS keystore support are selected and proved in #15.
- **Client-held encryption:** content-decrypting security-space keys remain with approved devices, plus independently protected owner recovery material. The remote relay may hold only authenticated ciphertext, nonsecret protocol state and key envelopes it cannot unwrap. Device loss, newly shared recipients, key-epoch rotation, key tampering, revoked members and all-keys-loss must be tested. A server-side identity reset cannot independently recover or decrypt this security space.
- **Trusted-host encryption:** an explicitly authorized host may decrypt permitted collections; it must protect service credentials and keys independently from ordinary data, use least-privilege operator roles, encrypt/authenticate backups, rotate compromised keys and verify restore. This is **not E2EE**. Its ability to see collection plaintext cannot imply permission for an external inference provider.
- **Recovery:** independent, verified recovery material is required for client-held content; if every valid client key and recovery factor is lost, no remote server can restore plaintext without violating the design. No hidden escrow or weakened recovery bypass. Local-only snapshots and portable archives are currently plaintext; hashes prove accidental integrity, not hostile-key authenticity or secrecy. Measure supported deployment RPO/RTO and crash/media faults under #40/#41, not by promising zero loss for a single device.
- **Sharing and revocation:** sharing requires explicit principal grants and recipient-wrapped keys where relevant. Removing someone blocks future service access and new encryption epochs but cannot destroy old offline copies. Index and cache authorization must observe revocation even for byte-identical content shared by other sources.
- **Compatibility:** #14 portable record IDs and #17 versioned sync semantics still apply. An opaque host cannot truthfully validate plaintext path/link conflicts it cannot inspect; enrolled decrypting clients perform those checks. No unreviewed cryptographic constructions, irreversible automatic class conversions, or claims that rotation securely deletes every historical backup.

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

| T15 | Critical | Household/shared knowledge leaks private information across principals via membership, content-deduplicated originals, search, logs or old caches | Deny-by-default principal/device/security-space/source evaluation; revoked membership and same-byte distinct-owner cases S02/S05/S17/S22/S23. #15/#28/#32 |
| T16 | Critical | Client-held ciphertext is silently uploaded/decrypted in a trusted-host class or a model/provider receives unapproved content after migration | Owner-authorized class transition, no implicit provider fallback, explicit key transfer and derived-data purge; S01/S06/S08/S09/S18/S21. #15/#19/#31/#32 |
| T17 | Critical | Server-side password reset, insecure key escrow or loss of all recovery factors defeats confidentiality or destroys the sole decrypting copy | Independent owner recovery material, compartmentalized keys, no privileged bypass, explicit irrecoverability; S10/S11/S12. #15/#40 |
| T18 | High | Malicious relay replays valid old encrypted state, swaps envelopes or suppresses events to trigger rollback, resurrection or false sync | Authenticated object context and key epochs, client durable freshness/epoch validation, reset with pending edits preserved; S06/S07/S14. #15/#19/#21 |
| T19 | High | SwiftUI-to-.NET child worker assumes secure caller/IPC and a different OS user or forged helper gains local authority | Real-process peer/handle and owner-private root validation; document limits of same-user malware; S19. #107/#215 |
| T20 | High | Unapproved account/collection policy or stale token lets remote host or agent run unattended, including unexpected cloud provider escalation | Independently scoped principal/device/agent grants, audience/issuer validation, consent-to-destination, safe revocation; S03/S04/S08/S16/S18. #15/#31/#32 |
| T21 | High | Claimed near-zero data loss hides asynchronous replication, incomplete ciphertext backup, or missing recovery keys | Explicit RPO/RTO and acknowledgment semantics, measured crash/restore/rekey faults; S10–S13/S20/S21. #40/#41/#46 |

| T22 | Critical | Owner-authored many-to-many `source_collections` mistaken for key/authorization scope, allowing organizational grouping or shared digest to escalate access | Enforce exactly one authoritative security space per source; UI grouping has zero grant authority; deny cross-space union and source hash existence leakage S23. #15/#28/#29 |

## Approved owner-local caller architecture — #129

The owner-approved decisions in [ADR 0021](../decisions/0021-owner-local-caller-security.md) select direct trusted first-party application calls (in-process initially), a **separate authenticated loopback boundary only for future integrations**, no ordinary-browser API access, future individually paired capture-only extensions, future separately authorized source-scoped read-only MCP agents, short-lived renewable and revocable credentials, and one isolated private store/runtime per OS user. The [security verification matrix](../testing/owner-local-caller-security.md) distinguishes the currently testable liveness-only API from the unimplemented listener, credential and OS isolation tests.

ADR 0021 is a separately accepted **owner-local** caller policy; it does not authorize remotely reachable private content, cross-user sharing, third-party data access, encrypted-at-rest claims or unapproved model inference. The SwiftUI macOS UI actually launches a .NET child worker over inherited pipes (ADR 0036), rather than an in-process C# host. Its OS-user, inherited-handle/worker execution and filesystem trust must be verified on the real device (#215/#107); a same-user process boundary is not isolation from malware running as that same user. The remote product requirements in #50 do not supersede the owner-local integration permission model. Those remain open in #13/#50. The present `Praxis.Api` host still has only liveness and no implemented authentication; existing plaintext catalog permissions do not protect against same-user compromise, and Windows ACL and real OS-peer isolation need verification before private endpoints can ship.

## Additional local capture, browser, voice and agent boundaries (planned)

These are requirements for future work, **not** evidence of implemented security controls:

- **Owner-local library:** accepting a directory or file means permission for the chosen source only, not for a whole home directory. The local service cannot assume that loopback implies authorization; browser origin bypass, DNS rebinding, alternate local users and token disclosure require explicit negative tests (#99/#100/#107).
- **Hostile input:** malformed PDFs/media/archives, code, game binaries, and fetched pages are untrusted. Never execute imported code or extraction instructions; bound file size, parser runtime, recursion and derived output, and do not silently upload content (#102/#103/#114).
- **Dictation:** microphone permission and active recording state are explicit; cancellation and retention must be tested. A transcript is a derived output with separate processing consent; no remote speech model receives audio by default (#109/#110).
- **Extension:** page scripts are untrusted, even on a tab the user chose. Paired browser capture needs narrow grants, cross-origin checks, content sanitization, an explicit queue and revocation, with no passive browsing-history surveillance (#111/#112).
- **MCP:** agent identities have separately scoped read and write capabilities. Retrieved notes/web pages cannot instruct the service to escalate permissions; tools require bounded arguments/responses and auditable approvals; no agent receives arbitrary disk access (#117/#118/#119/#32).
- **Deletion and index cleanup:** source revocation/deletion must retract unauthorized derived snippets, suggestions, cached responses and agent-visible results, even when identical bytes remain for another authorized source (#102/#106/#115/#118).

Owner product processing choices are complete in #50. Local first-party capture remains independently permitted within its reviewed trust boundary; remotely reachable private access requires #13's reviewed security contract and the implemented/verified #15/#19/#40/#41 controls. External provider disclosure and unattended agent side effects require independent #31/#32 approval regardless of deployment.

## Verified original-file publication boundary (S1b)

S1b issue #124 adds an explicitly invoked .NET stream-to-storage primitive. Only the caller supplies source bytes and absolute locator; no automatic source opening, watcher, browser permission, microphone access or remote transfer is added. The locator is **unverified provenance text** until the planned input-selection layer (#101/#102) validates that the stream came from a user-approved source.

The owner-selected data root is checked before capture. Staged originals are placed under private subdirectories, size-limited, hashed and flushed; a non-overwriting same-volume rename publishes them by lowercase SHA-256. Existing objects are rehashed before sharing. The catalog transaction comes after successful published-byte verification. A failed SQL transaction can leave an orphan verified object, and no automatic garbage collector is authorized yet. Retries and source-version updates are replay-safe for committed v2 receipts after S1c #126; the caller still supplies unverified source provenance.

Local filesystem and owner permissions are not a security boundary against another process with the same OS-user authority; the current implementation does not guarantee resistance to adversarial path substitution, hard power loss, Windows ACL misconfiguration or disconnected external media. A hash confirms bytes, not authenticity or access rights. The catalog, originals and derived content are **plaintext** until separately authorized encryption decisions. These limits remain in #50/#40/#107. The write primitive must not be exposed to browser pages or MCP agents without separate caller authentication and scopes.

## Explicit request receipts and interruption inspection (S1c)

S1c introduces caller-provided request keys and stable source/item revision history. A committed key returns the stored historical result **without re-reading the caller's stream**. This makes intentional retries repeatable, but does not authenticate source locators, prove a new stream matches the old stream or authorize a new actor to replay someone else's key. Future localhost, browser and MCP interfaces must bind request keys to an authenticated principal, source scope and expiration/revocation policy before exposing this API.

`LocalCatalog.Recovery.InspectAsync` reads catalog and object metadata and rehashes referenced original files. It can identify incomplete staging, orphaned publication, corrupt and missing references; it makes **no changes**. The scan is not a consistent snapshot across concurrent imports, and an orphan may still be in use. Retention policies, directory replacement risks, interrupted power/drive removal and complete media+metadata backups remain required under #100/#40. Do not expose source paths, file hashes, private content or inventory output to agents without #107/#117 access decisions.

## Coherent local snapshot trust boundary (S1d, PR #134)

The snapshot library uses SQLite's backup API for a committed catalog view, copies catalog-recorded originals into a private sibling staging directory, verifies the manifest and hashes, and refuses to publish or restore into existing final paths. A catalog digest is an integrity check, not a signature; a malicious same-user process able to replace the catalog and manifest can forge a self-consistent backup. The bytes and recovery metadata are **plaintext**. Owner-private Unix mode checks do not establish Windows ACL correctness.

Failures or interruption can leave \`.incomplete-...\` private directories. These are never completed backups and must not be automatically restored or removed without examining the last good copy. The backup excludes uncommitted staging and unknown object orphans, while retaining all committed source revision/receipt history. Restored incomplete request records stay inert; #102 must define future job recovery.

This PR provides library-level deterministic verification, not evidence of multi-GiB media, disk exhaustion, actual unexpected unmount/power loss, durable directory metadata on every filesystem or safe off-device encrypted recovery. Those remain #128/#40 and manual owner acceptance. Do not expose snapshot paths or destructive restore entry points to browsers/MCP without #107/#117 permission boundaries.

## Durable processing jobs and source revocation (S2a, PR #135)

SQLite v3 retains immutable original capture receipts and separately records derivative-processing work per item/version/processor. Workers must acquire a bounded, expiring lease and prove possession of its current token at completion. Cancellation or source revocation clears leases so late workers cannot update job state; it does not terminate arbitrary running parser code, delete originals, erase previous derivatives or revoke future API access by itself. All future result writes, indexing, cache retrieval and agent tools must enforce active source authorization **at time of access**, not assume that a completed job remains authorized.

Job failure metadata is limited to short, non-sensitive error codes; do not record source text, filesystem paths or exception output. Initial processing jobs are queued but there is **no active worker or untrusted file parser** in this slice. Owner-approved UI file selection (#101), bounded processor execution (#102/#103) and local caller trust (#129/#107) remain unimplemented. The direct local helper accepts explicitly supplied regular files/folders and rejects symlinked selected entries and children. It cannot establish caller consent, detect every symlinked ancestor outside the selected scope, or defeat a same-user path replacement between validation and opening. Existing catalog backups must continue to verify and restore v2 originals and receipts into v3 without dropping historical data; the v3 job table is included in new snapshots.

## Verification gates

**Before exposing a private-data sync endpoint:**

- [x] Local-only, client-held ciphertext and trusted-host *product modes* are expressly approved in closed #50.
- [ ] Mode-specific key custody/recovery design is reviewed under #13; actual keys, authorized enrollment, rotation, failover and recovery are independently tested under #15/#40/#41.
- [ ] Device enrollment, authorization, token revocation and negative cross-identity tests pass (#15).
- [ ] TLS/non-loopback deployment boundary and secret storage are verified (#41).
- [ ] Content and attachment limits, validation and replay behavior pass adversarial tests (#19, #21).
- [ ] An export, encrypted backup and recovery procedure exist with a successful restore test (#14, #16, #40).
- [ ] Logs and error responses are tested against sensitive-content leakage.

**Before permitting indexing, remote inference or action tools:**

- [ ] Processing consent and provenance are enforced by **security space and source** (not many-to-many organizational collections) at original, derivative and query boundaries (#15/#27/#28/#31).
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

## #13 spike exit versus release evidence

This threat model selects and reconciles the **design** of principal/device/grant, collection class, content-key ownership, backup/recovery and user-sharing controls with accepted ADR 0003 and ADR 0006. The completed [#50](https://github.com/berniemackie97/Praxis/issues/50) authorizes that product direction. The [S01–S23 security scenarios](../testing/remote-trust-security.md) are executable acceptance **contracts** for #15, #19, #32, #40, #41 and other owners; no nonexistent remote implementation can pass them today.

For the #13 spike, design review must verify that every cross-principal, class-transition, inference, key-loss, replay and recovery threat has a mitigation owner and a negative test. **Do not require #15's not-yet-built remote implementation to pass S01–S23 before closing #13**—that would deadlock its native dependency graph. Conversely, closing #13 does not waive a single later runtime gate. Retain current real API denial tests as baseline only; execute full suites and physical/VM proof when the respective implementation ships and CI runners operate (#137).
