# Device proof v1 — host-side identity registry (#15)

**Implementation status:** The durable registry, local operator provisioning, P-256 challenge verification, security-space mapping, grant/revocation checks and deterministic tests exist in this implementation slice. **No remote API, issuer integration, encrypted key envelope, backup/recovery path or two-client sync is implemented or authorized.** Issue #15 remains open.

The registry is intentionally a separate `identity.sqlite` database under an existing, private **security-host directory**; it is never added to the owner-local plaintext catalog, and it does not change existing capture, organizational collection, indexing or liveness routes. POSIX roots must be owner-only. Windows effective ACL and external-volume security are *not* proven by this implementation. The process holds an exclusive file lease, uses SQLite WAL, foreign keys, integrity verification and `synchronous=FULL`. A restart preserves enrollment, grants and consumed challenges.

## Authority and operation

- `SecurityRegistry.Operator` is **trusted out-of-band host administration** only. The invoker is responsible for authenticating and authorizing the operator before creating principals, enrolling devices, assigning authoritative security spaces, granting membership or revoking credentials. Its methods are not remote self-enrollment, account recovery, authenticated user administration or an identity provider. Never expose/inject this surface into untrusted HTTP/MCP handlers.
- `SecurityRegistry.DeviceProofs` is the verifier surface. An owner/operator registers a **P-256 ECDSA public key in SubjectPublicKeyInfo DER** for each device. The private signing key stays on that device; this storage component never generates, imports or persists a device private key.
- `security_spaces` are **remote security boundaries** with client-held-encrypted or trusted-host class metadata. The registry deliberately does not accept local-only content as remote space data. `source_spaces` permits exactly one authoritative space per source ID; the many-to-many `source_collections` UI grouping is unrelated to authorization. No source content or decrypting keys live in this registry.
- Space owner has read/write authority; explicit member grants can allow read and/or write. The operator may revoke a device, disable a principal or revoke/regrant a member. Each member grant has a monotonic grant version, so a revoked proof cannot revive when membership is later restored. Grant existence is never inferred from organization membership or a matching digest.

## Signed challenge format

The verifier issues a random 128-bit challenge ID and cryptographically random **32-byte nonce** for an enrolled device, one space, optional source ID and one operation (`Read=1`, `Write=2`). The issue operation validates current principal/device/grants/source mapping, caps each device at **16 outstanding challenges**, and persists an expiry of **30 seconds**. The challenge bytes to sign are the UTF-8 encoding of these lines with LF (`0A`) separators, including a final LF:

```text
Praxis.DeviceProof.v1
<installation UUID, lowercase 32 hex digits>
<challenge UUID, lowercase 32 hex digits>
<device UUID, lowercase 32 hex digits>
<security-space UUID, lowercase 32 hex digits>
<source UUID in lowercase 32 hex digits, or '-' when absent>
<decimal permission number (1 or 2)>
<decimal grant version (0 for owner)>
<expiry as Unix seconds in UTC>
<standard Base64 encoding of 32-byte nonce>
```

Sign the **exact returned bytes** using ECDSA with SHA-256 and a **64-byte IEEE P1363 fixed-field `r || s` signature**, not DER-encoded ECDSA signature bytes. A device cannot reuse a signed request for another space, source, operation, host installation or challenge. The service atomically consumes a valid-format challenge attempt, including an incorrect signature. It rechecks principal/device/grant/source state and grant version, and refuses expired, replayed, revoked or cross-scope proofs.

`VerifiedDeviceProof` is only a short-lived, in-process request result; it is **not** a transferable bearer session or proof of OAuth account authentication. `IsCurrent` checks the registry and expiry again before the host attempts a protected operation. This is necessary but **not sufficient for remote-data security**: the future #19 transport must bind the verified principal/device/space/source context to the actual persisted record/event/attachment operation without a time-of-check/time-of-use grant race, and independently enforce resource quotas, TLS, client issuer/audience validation and connection identity.

## Evidence and remaining work

`tests/Praxis.Storage.Tests/Security/SecurityRegistryTests.cs` exercises signed one-use access, mismatched device keys, role/source/space denials, member grant revocation and regrant epochs, revoked device and disabled principal, persistence and replay across restarts, expired/rate-bounded challenges, P-256 format constraints, cross-installation rejection and private directory validation.

These are **registry-level** controls contributing to S02/S03/S04/S05/S23; they do not establish full S06/S07/S10/S11/S16/S22 acceptance from [#13's test matrix](../testing/remote-trust-security.md). Next, #15 must integrate a properly authenticated self-hostable OIDC/account enrollment route, sender-constrained device/session binding, authenticated client-held key envelopes and independent owner key recovery, and require server-side negative authorization tests against the actual #18/#19 private transport. #40/#41 own encrypted backup, restore and deployed host safety. A single-machine WAL write is not a replicated zero-loss promise. Do not expose private networking or mark #15 complete until those gates pass.
