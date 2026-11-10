# Owner-approved device enrollment, client trust, and protected execution v1

**Implementation status:** Registry-level M1 security contract for [#15](https://github.com/berniemackie97/Praxis/issues/15), with tests and an execution-facing handoff for [#19](https://github.com/berniemackie97/Praxis/issues/19). This does **not** expose any private-data HTTP endpoint, operate a certificate issuer, implement an interactive approval screen, or establish a complete deployed household sync system. **Do not close #15/#53 before final candidate execution and feature acceptance.**

## Enrollment authority and transaction

A trusted owner already holding a valid **space-scoped write proof** may approve a new device for the owner principal or a principal with an **active read-authorized member grant in that exact security space**. An ordinary write-authorized household member cannot approve other devices, and a local organizational collection is not an authorization scope.

1. The proposed device generates a new **P-256 ECDSA signing key** and **RSA-3072 OAEP wrapping key**, and uses its signing key to attest the RSA SubjectPublicKeyInfo as defined by [space-key envelope v1](space-key-envelope-v1.md). No private key is sent to Praxis.
2. The owner requests a pending admission against a specific target principal/space. The registry persists a fresh 128-bit challenge UUID, **new 128-bit device UUID**, 32-byte cryptographically random nonce and 2-minute expiry, bound to that owner signing device and exact applicant public keys. A maximum of **8 pending challenges per owner device** is enforced.
3. The owner and applicant each sign the **exact same** UTF-8 LF-terminated challenge message with their independently held P-256 signing keys, SHA-256 and 64-byte IEEE P1363 signatures. The transcript is:

```text
Praxis.DeviceApproval.v1
<installation UUID, lowercase 32 hexadecimal>
<challenge UUID, lowercase 32 hexadecimal>
<new device UUID, lowercase 32 hexadecimal>
<security-space UUID, lowercase 32 hexadecimal>
<applicant principal UUID, lowercase 32 hexadecimal>
<approving owner device UUID, lowercase 32 hexadecimal>
<applicant signing public key SPKI, standard Base64>
<applicant RSA wrapping public key SPKI, standard Base64>
<applicant wrapping-key binding signature, standard Base64>
<expiry as Unix UTC seconds>
<32-byte nonce, standard Base64>
```

4. On completion, the registry **rechecks the current owner's device proof, ownership, target principal status and current member grant**, atomically consumes the challenge even after bad signatures, and inserts the new device identity, verified wrapping-key binding and persisted dual-signed receipt in one transaction. Duplicate keys/IDs, revoked membership, another space/owner and replayed or expired attempts do not enroll a device.
5. Enrollment sets `rekey_required` for the principal's owned/readable spaces. A new owner-controlled [space-key envelope](space-key-envelope-v1.md) publication at the next epoch is required before current envelopes or protected client-held data may be accessed.

**No amount of approval metadata means the host can decrypt client-held content.** The private signer, RSA decrypting key, 32-byte content recovery secret and actual content key remain with authorized clients.

## Independently pinned client trust store

`ClientDeviceTrustStore` is a separate **owner-private local SQLite database** in a directory provided by the client, never the server's `identity.sqlite` registry. The owner initially supplies the installation ID, owner device ID and **independently verified, pinned** owner P-256 public key; the store rejects later attempts to reopen against a different installation/key and cannot bootstrap from a host-provided device directory without independent verification.

A client calls `Accept` only after verifying the owner and applicant signatures on a **still-fresh receipt** and the exact owner device/installation/space/recipient identities. An accepted key pin survives process restart and receipt expiry. The caller may explicitly revoke a pinned recipient only while holding the local owner's P-256 signer. A revoked recipient is removed from `TrustedRecipients(spaceId)`; the store never silently re-enables it from a replayed enrollment receipt.

`ClientSpaceKeys.Protect` requires that owner-controlled pinned-key map for **every device receiving a wrapped content key**. Even when a malicious or corrupted host inserts a new enrolled device into its recipient directory, the client refuses to encrypt if that device is not independently pinned. This check is cryptographic **recipient identity authorization**, not proof that the host's permissions/revocation history is honest: the client must also persist and review which household members are actively allowed to receive content at each epoch, and the future UI must present pending approvals and revocation changes to the user.

A compromised same-UID process or stolen owner signing private key can defeat the client's local trust root. A client additionally stores its highest **verified current content-key epoch** in its private SQLite trust database. The `ClientSpaceKeys.UnlockForDevice` and `UnlockWithRecovery` overloads taking `ClientDeviceTrustStore` verify the independently pinned owner signature, decrypt and check the commitment, and only then advance that durable epoch floor. A previously valid signed **current-key** envelope cannot roll the client back after restart. Historical ciphertext recovery deliberately uses a separate authenticated recovery archive and explicit expected epoch; it cannot be mistaken for the current epoch.

The owner may independently pin a recovery-signing P-256 key after **both** current owner and recovery signers approve its exact installation/device/version transcript. A lost-last-device replacement is accepted into the local owner trust root only when **that recovery private key and the new owner private key** sign the exact old-owner/new-owner transition. This clears previously approved member pins for deliberate reapproval, retains the durable epoch floor and works after restart. Recovery-signing and content-decryption secrets remain distinct.

This implementation does not claim hardware-key custody, Windows ACL verification, secure user-interaction approvals or complete malicious-host defense. Those remain explicit deployment/UX gates, not implicit effects of SQLite WAL.

## Source-scoped protected-data handoff

`AuthenticatedDataAccess.Execute` accepts a **validated mTLS device session**, one-use signed device proof, exact source ID, requested read/write operation and current content-key epoch. It requires:

- Same current principal and device identity in TLS and the signed operation proof
- A matching **source-scoped** proof and requested permission; space-only proofs cannot authorize data access
- Source belongs to the authenticated security space, not an organizational collection view
- The source's space is **client-held encrypted**, has a committed current key epoch, and is **not fenced for rekey**
- Requested epoch equals the latest committed epoch; future/old epochs fail closed

It supplies an immutable **`AuthorizedDataScope`** to the callback, containing the validated principal, device, security space, source, operation and key epoch. Data access implementations must use this scope instead of untrusted request fields. It then holds the registry's exclusive in-process lock throughout the supplied **synchronous** data operation, preventing an interleaving grant mutation in that process. This is deliberately *not* a private-data store, network push/pull implementation, async distributed authorization guarantee or cross-database atomic commit. The #19 transport must use the gate with its own transactional data operation, deny leaks in errors/conflicts/search/attachments/cursors, and test cancellation/retry/data-durability semantics. **Do not expose the existing operator-only provisioning methods to remote request handlers.**

Trusted-host plaintext processing is intentionally denied by this v1 gate until its own approved key-custody and processing contract exists. No locally only content is authorized for sync.

## Release evidence and explicit non-goals

`ApprovedDeviceEnrollmentTests` covers owner/member two-party signatures, revoked membership during pending admission, replay consumption, independent client trust bootstrapping and restart, malicious host recipient insertion, foreign owner key, client-side revocation, and certificate/session/source/permission/epoch rekey enforcement. `ClientTrustEpochTests` separately rehearses two independently persisted trust directories, grant revocation and signed key rollback denial, schema v1→v2 trust upgrades, and last-device recovery including recovery-authorized owner pin rotation. `SecurityRegistryMigrationTests` covers historical v1 and v3 registries upgrading to schema v4.

No new CA or bearer-token issuer is introduced; [ADR 0038](../decisions/0038-private-device-certificate-identity.md) retains the separate self-hosted **short-lived pinned-CA mTLS** transport direction. No network endpoint or automatic certificate issuance, renewal, account password flow, UI confirmation, OS keychain, two-physical-device workflow or #19 authenticated remote data sync is yet implemented. No test is marked passed without actual execution on the final PR commit.

**Acceptance evidence still required:** run all Release tests, `dotnet format`, Python roadmap tests, security scenarios S02–S07/S10–S11/S16/S22–S23 and real client/host CA setup. Demonstrate signing/pinning approval on at least two **independently persisted physical client environments**, proper Windows/macOS permissions and revocation/recovery across process/device restarts. Only then reconcile #15 and parent [#53](https://github.com/berniemackie97/Praxis/issues/53).
