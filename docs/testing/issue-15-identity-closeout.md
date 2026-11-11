# Issue #15 — identity and key-custody closeout evidence

**Status:** implementation/test source under [#15](https://github.com/berniemackie97/Praxis/issues/15), not an execution or production security certification. Close #15 and [feature #53](https://github.com/berniemackie97/Praxis/issues/53) only after the final candidate commit passes its release tests, audit and explicitly approved platform evidence. The remote push/pull data transport is owned by #19 and must not be incorrectly counted as part of this M1 implementation.

## Offline issuer and client trust contract

Praxis uses the mTLS device-certificate path in [ADR 0038](../decisions/0038-private-device-certificate-identity.md), rather than a home-grown bearer token or mandatory subscription service. Operator-held P-256 private CAs may issue 24-hour X.509 certificates through `DeviceCertificateIssuer.Issue` only when a signed PKCS#10 request proves possession of the exact P-256 key already enrolled by the owner or via a separately signed household approval receipt. The issuer refuses unapproved member devices, substituted keys, a revoked or expired signed device proof, malformed request, incorrect issuer signing authority and unsupported CA key types. Its X.509 certificate is client-auth-only; the CA key is never stored in `identity.sqlite`. The returned leaf has no private key; the client retains its own signer.

The `DeviceCertificateIdentity` verifier requires a pinned private root and checks the enrolled signer, client EKU, key usage, validity, current account/device status and signed operation proof. **The operator-only issuer method and the verifier are implemented libraries, not a public certificate-enrollment endpoint or a running CA**. Production TLS entry and operational revocation checking must be configured and evidenced under #19/#41, not inferred from `AuthenticateForTest` (which deliberately skips CRL checking only for ephemeral unit-test CAs).

The client `ClientDeviceTrustStore` stores independently verified recipient signing identities and highest **verified current** space-key epochs on owner-private durable SQLite files. The store is not created from a host-returned key directory; users must independently bootstrap the owner identity. The `ClientSpaceKeys` overloads taking the store verify the pinned owner signer, decrypt the device/recovery envelope, verify its commitment and advance the highest trusted current epoch. An otherwise valid older current-key envelope is then rejected even after process restart.

If the last enrolled owner device is lost, an independent offline P-256 recovery signer and a new device signer must authorize enrollment. The old owner recovery envelope and independently retained 32-byte content recovery secret can decrypt *historical* keys. An existing client trust store transfers its pinned owner identity only when **both** the previously pinned recovery signer and new owner signer sign a domain-separated old→new device/key transcript. The transfer clears old recipient pins, forcing deliberate review before new sharing; it does not reset previously accepted key-epoch floors. Old downloaded content is not remotely erased. Loss of all private device keys and both recovery authorities and content secrets remains irrecoverable.

These are security contracts; client-side **interactive** approval, OS credential storage/hardware protection, remote trust bootstrap and issuer/deployment operations have not been completed by adding these classes. Their existence must not be used to claim end-to-end encryption from an untrusted device directory without verifying pins.

## Acceptance-case matrix — do not mark Passed before execution

| Scenario | Executable source / current scope | Release evidence needed |
| --- | --- | --- |
| S02 principal/space/source scope | `ApprovedDeviceEnrollmentTests.MismatchedCertificateIdentityPermissionSourceAndEpochNeverRunDataOperation`, `AuthenticatedDataAccess` | Full source-level count/search/cache isolation is #19/#32 data integration; ensure generic denial shape and no callback |
| S03 copied IDs/unauthorized device | `ApprovedDeviceEnrollmentTests`, `DeviceCertificateIdentityTests`, `SecurityRegistryTests` | Signed applicant/owner, independent pin, issuer key mismatch, challenge replay and negative checks |
| S04 expired/revoked proof | `DeviceCertificateIdentityTests`, `RecoveryEnrollmentTests`, `SecurityRegistryTests` | Time and revocation; real production mTLS and CA revocation are #19/#41 |
| S05 revoke shared member | `ClientTrustEpochTests.TwoPersistedClientsRejectOldSignedEpochAfterRevocationAndRestart`, `SpaceKeyDistributionTests` | New epoch excludes removed member; old cached plaintext cannot be erased |
| S06 client-held opaque keys | `SpaceKeyDistributionTests`, `ClientSpaceKeys` and `ClientDeviceTrustStore` | No host decryption key/private signer, malicious unsigned recipient rejected; actual ciphertext transport #19 |
| S07 signed rollback | `ClientTrustEpochTests` with two separate persistent client directories | Accept epoch 2, restart, reject old validly signed epoch 1; distinguish historical content recovery |
| S10 lost-last-device recovery | `ClientTrustEpochTests.LastDeviceRecoveryTransfersPersistedOwnerTrustWithoutHostEscrow`, `RecoveryEnrollmentTests` | Independent recovery signer and content secret, signed owner handoff, rekey and restart |
| S11 lost-all-secrets irrecoverability | `RecoveryEnrollmentTests` negative challenges and AES-GCM wrong-secret tests | Verify no admin/backdoor escrow; loss scenario explicitly irrecoverable; backup validation #40 |
| S16 browser/OIDC PKCE | **Not applicable to selected mTLS-only device protocol** | If browser/OIDC sign-in is introduced, S16 becomes mandatory with auditable PKCE state/nonce/audience checks; cannot claim OIDC exists |
| S22 logs, role separation | Owner-only archive tests, no private API routes | Real log/backup/operator data exposure is #19/#40/#41 and must remain a release blocker there |
| S23 organizational views | `AuthenticatedDataAccess` operates on `source_spaces`, never `source_collections` | Derived data, count/search/digest isolation needs real #19/#32 consumers; no remote endpoint exists here |

## Required owner verification — exact candidate SHA

Use a separate worktree. Do not commit real recovery material, CA private keys, household data or actual security backup files. Synthetic keys and fixtures only.

```sh
git fetch origin feat/issue-15-identity-closure
git worktree add --detach ../Praxis-identity-15 \
  origin/feat/issue-15-identity-closure
cd ../Praxis-identity-15
git rev-parse HEAD
dotnet --info
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test tests/Praxis.Infrastructure.Tests/Praxis.Infrastructure.Tests.csproj \
  -c Release --no-restore \
  --filter 'FullyQualifiedName~Security'
dotnet test Praxis.slnx -c Release --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
python3 -m unittest discover -s scripts/github -p 'test_*.py'
```

Return the actual commit SHA, OS/runtime, complete errors for any failed command, targeted/full test totals, formatter exit status and Python results. The complete fail-fast chain must exit 0 before claiming the Mac baseline is green. Cross-OS GitHub Actions jobs must **execute**, not merely queue; while [#137](https://github.com/berniemackie97/Praxis/issues/137) remains owner-deferred, record any explicitly accepted alternative validation and its real limitations. Do not silently waive cross-platform evidence.

Before closing #15/#53, additionally confirm whether the intended M1 acceptance is the **testable self-hosted identity library and device protocol** or a usable shipped owner/account enrollment UI on actual devices. The repository still has no remote private-data route; #19 will be responsible for that transport. A library can be complete while deployment still needs separate gating, but a claim of an operational end-user workflow requires more than xUnit synthetic certificate fixtures.

## Known limitations to carry into #19 / #40 / #41

- The offline certificate issuer has no network listener, automated ACME/renewal, CRL/OCSP service, PKI hardening, human approval UI or OS secure enclave/keychain integration. Production CA lifecycle belongs to deployment/integration, not to the proof verifier.
- `AuthenticatedDataAccess` holds only a single host registry lock while invoking synchronous caller work. It is not cross-database atomicity or a remote durable data store. #19 must independently enforce the same scope for every stored record/event/attachment, with normalized denials and bounded request processing.
- Historical content uses historical signed envelopes/epochs; the current-key floor intentionally rejects a downgraded **current** offer, not a documented explicit historical decrypt.
- Host-provided public-key claims are never sufficient proof of household consent. A newly recovered owner key is independently approved by the recovery signer before the trust pin can change.
- The reference client trust store requires an owner-private directory; physical OS ACL checks on Windows and same-user malware resistance are not established by these synthetic tests. No server master-decryption key exists.
