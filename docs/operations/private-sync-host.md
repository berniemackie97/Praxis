# Private synchronization host — D1 candidate (#19)

**Status:** implementation proposal; no owner-managed D1 deployment is authorized or certified. This is a separate executable from the D0 desktop worker and health-only API.

## Surface and trust

The only private routes are POST /v1/sync/challenge, POST /v1/sync/submit and POST /v1/sync/pull. The Kestrel listener requires TLS 1.3, a real handshake client certificate chained to the operator-pinned device CA, and current enrolled identity. Each operation needs a fresh one-use signed 30-second challenge with device/space/read-or-write scope. A signed request never grants itself permission through a UUID or forwarded certificate header. Grant/device revocation, owner-approved member enrollment, content-key epoch and pending rekey are rechecked under the identity-registry gate during each synchronous transaction.

The host permits **client-held ciphertext only**. AES-256-GCM client encryption uses a random 96-bit nonce, a 128-bit tag and authenticated data binding security space, log epoch, record ID, resulting revision, operation action and key epoch. Use the existing independently pinned client key-envelope/trust workflow to obtain the content key. Persist the exact encrypted bytes and operation UUID in the local pending queue before first send; recomputing ciphertext after a lost acknowledgment changes the operation digest. The host cannot validate hidden paths/Markdown/attachment links and must never receive plaintext projections from INoteSyncExchange. It also cannot prove a malicious authorized device actually encrypted its supplied bytes. Enrolled clients must validate every decrypted item and hidden metadata constraints.

## Operator-controlled configuration

The service is NOT enabled by installing the owner-local application. Set these environment variables outside the repository:

| Variable | Meaning |
| --- | --- |
| PRAXIS_SYNC_IDENTITY_DIR | Absolute existing private owner/operator security registry |
| PRAXIS_SYNC_DATA_DIR | Absolute existing private sync directory, separate SQLite WAL/FULL and lock |
| PRAXIS_SYNC_DEVICE_CA | Absolute public pinned device CA certificate, never private CA key |
| PRAXIS_SYNC_TLS_PFX | Absolute owner-private server TLS key/cert PKCS#12 |
| PRAXIS_SYNC_TLS_PASSWORD_FILE | Absolute owner-private PFX password file |
| PRAXIS_SYNC_BIND_IP | Literal address, defaults to 127.0.0.1 |
| PRAXIS_SYNC_PORT | Decimal 1024–65535, defaults to 5443 |
| PRAXIS_SYNC_ALLOW_PRIVATE_BIND | Set to 1 to enable non-loopback binding |

On Unix, use 0700 identity/sync directories and 0600 credential files. The host refuses linked credential files and exposed credential permissions. Neither the identity registry nor sync store creates remotely enrollable operator accounts. Prepare a distinct private CA and server leaf outside the repo; keep the issuer private key offline.

With identity/data-directory variables set, provision a selected owner-approved security space and immutable collection epoch **without opening a listener** using:

    dotnet run --project src/Praxis.SyncHost -c Release -- provision <space-guid> <epoch-guid>

After the TLS variables are set, start the separate network process using:

    dotnet run --project src/Praxis.SyncHost -c Release --no-build

A VPN does not provide device authorization. Never expose this candidate to the internet merely by setting PRAXIS_SYNC_ALLOW_PRIVATE_BIND. Review ingress, certificate issuance/revocation, storage and approved host obligations under #65.

## Limits and caveats

- TLS 1.3, mandatory client certificates, strict pinned CA and fresh signed proof at every data operation. **CA CRL/OCSP is not yet implemented**; local registry device revocation is immediate and distinct from CA revocation.
- Request body at most 96 KiB, opaque payload at most 64 KiB; up to 100 events/page, 32 concurrent TLS connections, eight executing requests, 15-second request time budget, 120 requests/minute/client certificate with zero rate-limit queue.
- Persistent SQLite limits: 10,000 accepted events/security space, 128 MiB of counted ciphertext. These are bounded safeguards, **not measured throughput/resource SLOs**.
- Atomic record/event/receipt/sequence commit with exclusive pull cursor. A disconnect may occur after commit; an identical retained operation returns the exact old receipt on retry with a new signed challenge.
- Current tombstones carry no copied previous ciphertext. An old-key record or event causes ResetRequired after rekey; no automatic re-encryption or snapshot rebootstrap exists. No event compaction/retention-floor expiry currently exists.
- No real authenticated attachment upload/link transactions (#20), offline journal ciphertext bridge and crash-recovered device convergence (#18), host-wide coordinated identity/sync/keys recovery (#65), or production D1 approval is in this PR. Invalid status responses and safe log filters must not be represented as complete host operational audit/retention.

## Acceptance before #19 closure

Run exact-commit locked restore, Release build, targeted real Kestrel TLS socket/AEAD two-device tests, full solution tests, format and roadmap checks, then record clean/dirty worktree and SHA. Test a selected approved private network host with independently issued real certs, cert and grant revocation, physical disconnect/cancellation, resource limits, encrypted attachment integrity, snapshots, key rotation and multi-device offline recovery; verify #65 host operations. GitHub Actions runner issue #137 must be addressed separately. Keep #19 OPEN until the actual scope is demonstrated.
