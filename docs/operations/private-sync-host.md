# Private synchronization host — D1 candidate (#19)

**Status:** implementation proposal; no owner-managed D1 deployment is authorized or certified. This is a separate executable from the D0 desktop worker and health-only API.

## Surface and trust

The private routes are POST /v1/sync/challenge, POST /v1/sync/submit, POST /v1/sync/pull, POST /v1/sync/snapshot, POST /v1/sync/rotation/stage and POST /v1/sync/rotation/commit. The Kestrel listener requires TLS 1.3, a real handshake client certificate chained to the operator-pinned device CA, and current enrolled identity. Each operation needs a fresh one-use signed 30-second challenge with device/space/read-or-write scope. A signed request never grants itself permission through a UUID or forwarded certificate header. Grant/device revocation, owner-approved member enrollment, content-key epoch and pending rekey are rechecked under the identity-registry gate during each synchronous transaction.

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

To compact one provisioned space's event history, stop the host process, retain a verified backup of the sync database and identity registry, verify the target cursor and record projection, and explicitly run:

    dotnet run --project src/Praxis.SyncHost -c Release -- prune <space-guid> <last-sequence-to-remove>

This command uses the same exclusive sync-store lease, refuses a sequence beyond the committed head or before the existing floor, retains the latest encrypted record projection and idempotent receipts, and refuses compaction if the recoverable projection exceeds the local recovery budget. It does not rotate keys. This operation is irreversible without the operator's verified backup.

After the TLS variables are set, start the separate network process using:

    dotnet run --project src/Praxis.SyncHost -c Release --no-build

A VPN does not provide device authorization. Never expose this candidate to the internet merely by setting PRAXIS_SYNC_ALLOW_PRIVATE_BIND. Review ingress, certificate issuance/revocation, storage and approved host obligations under #65.

## macOS TLS 1.3 client support (.NET 10)

.NET 10's Network.framework integration enables **client-side only**
TLS 1.3 on macOS; the inbound Kestrel/SslStream implementation still cannot
serve TLS 1.3 on macOS. A TLS-1.3-only client and TLS-1.3-only Kestrel listener
running on the same Mac cannot complete a handshake. Their error -9836 is
Apple's `errSSLPeerProtocolVersion`; disabling certificate validation cannot fix it.

The **production** `PrivateSyncHost.Build` requires TLS 1.3 and rejects macOS.
Host D1 on a supported Linux/Windows server, not the Mac desktop worker.
For macOS-only local test execution, an **internal** mode restricted to
`IPAddress.Loopback` uses TLS 1.2 solely for the disposable mTLS integration
host and companion local test client. This is not an operator setting,
production fallback or an Internet-exposed TLS 1.2 listener. The test suite
asserts the negotiated protocol and rejects the other protocol, and verifies
both trusted and rejected device certificates; Linux/Windows tests must
exercise the actual TLS 1.3 production code path.

`OpaqueSyncTransportClient.Connect` enables .NET 10 Network.framework-backed
outbound TLS on macOS before constructing its HTTP handler, requiring TLS 1.3
for connections to a supported production host. That setting applies to the
**whole process's outbound** TLS backend, not Kestrel's inbound backend.
If a future first-party desktop client has already initialized outbound TLS
before calling `Connect`, configure the switch at application startup instead:

```sh
DOTNET_SYSTEM_NET_SECURITY_USENETWORKFRAMEWORK=1 dotnet test tests/Praxis.SyncHost.Tests/Praxis.SyncHost.Tests.csproj -c Release
```

This command is only a diagnostic verification option, not a substitute for
testing the code's normal path **without** that environment override. The
Network.framework backend has different buffering, cancellation and
hostname-handling behavior, so the first-party caller must test those behaviors
before integrating it with other outbound services. Do not set a machine-wide
switch without reviewing other outbound HTTPS clients.

The focused regression checks that the test listener negotiates TLS 1.2
on macOS loopback and TLS 1.3 on a supported server. It also requires an
actual authenticated request, rejects an unsupported protocol and refuses
missing/untrusted client certificates. A macOS test-only TLS 1.2 success
**does not certify production TLS 1.3**; verify that separately on Linux/Windows.

## Limits and caveats

- TLS 1.3, mandatory client certificates, strict pinned CA and fresh signed proof at every data operation. **CA CRL/OCSP is not yet implemented**; local registry device revocation is immediate and distinct from CA revocation.
- Request body at most 96 KiB, opaque payload at most 64 KiB; up to 100 events/page, 32 concurrent TLS connections, eight executing requests, 15-second request time budget, 120 requests/minute/client certificate with zero rate-limit queue.
- Persistent SQLite limits: 10,000 accepted events/security space, 128 MiB of counted ciphertext. These are bounded safeguards, **not measured throughput/resource SLOs**.
- Atomic record/event/receipt/sequence commit with exclusive pull cursor. A disconnect may occur after commit; an identical retained operation returns the exact old receipt on retry with a new signed challenge.
- The operator can explicitly prune event history through a committed sequence only while the retained ciphertext projection fits the client recovery budget and no key rotation is staged; the transaction preserves current encrypted records and durable operation receipts. Expired cursors then invoke a proof-authorized fixed-head encrypted snapshot, with a maximum of 16 records per page and 10,000 records / 24 MiB of ciphertext per client recovery. A concurrent write invalidates the pinned head and aborts recovery without a checkpoint change. Multi-page recovery obtains a new signed challenge per page and paces requests under the host's per-certificate limit; HTTP 429 responses are retried with a bounded wait. This makes large recovery slower than incremental paging but avoids destructive partial progress. The host never decrypts paths or Markdown. Old legacy empty-ciphertext tombstones are rejected by the encrypted client; content-key rotation still requires a coordinated re-encryption/rebootstrap protocol and is not automatic.
- Offline note-journal ciphertext, incoming collision handling, and fixed-head snapshot recovery exist under #18. This PR proposes owner-mediated key-epoch staging/atomic ciphertext replacement, but requires exact-head Release verification and real-world acceptance. Production device/network recovery under #18/#65, authenticated attachment upload and atomic references (#20), and production D1 approval remain outstanding. Error response policy alone does not constitute full host operational audit or retention readiness.

## Remaining production acceptance beyond the bounded #19 transport

Run exact-commit locked restore, Release build, targeted real Kestrel TLS socket/AEAD two-device tests, full solution tests, format and roadmap checks, then record clean/dirty worktree and SHA. Test a selected approved private network host with independently issued real certs, cert and grant revocation, physical disconnect/cancellation, resource limits, encrypted attachment integrity, snapshots, key rotation and multi-device offline recovery; verify #65 host operations. GitHub Actions runner issue #137 must be addressed separately. Keep the outstanding #18, #20 and #65 release gates open until their independently verified acceptance is demonstrated.
