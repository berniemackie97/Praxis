# Independent recovery-device enrollment v1

**Status:** executable **registry-level** recovery, not an internet-facing account/session issuer, OIDC integration, trusted cross-user key-pinning ceremony, or deployed multi-device synchronization. Introduced under [#15](https://github.com/berniemackie97/Praxis/issues/15). Keep the [feature #53](https://github.com/berniemackie97/Praxis/issues/53) completion gate open until its entire authentication and authorization acceptance is evidenced.

## Two different recovery credentials

1. A **P-256 ECDSA recovery signing keypair**, generated and retained outside the security host, authorizes enrollment of a new device when every ordinary device is unavailable. The host stores only the public key and its monotonically increasing version. **The private signing key must never be stored in the host database or cloud account settings.**
2. A **32-byte random AES-256-GCM content recovery secret**, also independently stored by the owner, decrypts historical security-space key recovery envelopes. The registry stores only the signed encrypted envelopes. **Possessing the recovery signing key does not decrypt content; possessing the content secret alone cannot enroll a replacement device.**

These credentials are separate on purpose. A server password reset, identity-provider account administration, or a database snapshot alone must not be described as content-key recovery. A host administrator that can mutate the identity database can disrupt service or manipulate enrollment metadata; the client still must independently pin authorized signing identities to prevent a compromised host from causing later key disclosure.

## Initial registration and rotation

A currently enrolled **space-owning device**, holding a valid 30-second space-scoped write proof, may register the account's recovery signing public key. It must submit two independent **P-256/SHA-256 IEEE P1363** signatures: one by that enrolled owner's signing private key and one by the proposed recovery private key. Both sign the exact UTF-8 LF-terminated message below:

```text
Praxis.RecoveryRegistration.v1
<installation UUID, lowercase 32 hexadecimal digits>
<principal UUID, lowercase 32 hexadecimal digits>
<owner signing device UUID, lowercase 32 hexadecimal digits>
<recovery public-key version, invariant decimal integer>
<recovery P-256 SubjectPublicKeyInfo, standard Base64>
```

The first key version is 1. Changing it requires **three** signatures on the next-version message: current owner device, proposed new recovery key, **and previous recovery key**. The registry rejects any unsigned recovery-key change. Re-registration removes outstanding replacement challenges, and old recovery credentials no longer authorize new enrollments. The offline owner must separately back up the new private key and pinned public-key fingerprint.

## Replacement challenge

A replacement device generates a **fresh P-256 signing keypair and independent RSA-3072 recipient keypair**, then signs its binding message as specified in [space key envelope v1](space-key-envelope-v1.md). The registry issues an **untrusted until signed**, bounded request:

- 128-bit random enrollment ID and cryptographically random 32-byte nonce
- Exact principal, replacement signing/public-wrapping keys, binding signature and current recovery-key version
- 2-minute expiry, at most **8 outstanding challenges per principal**

Both the independently held recovery signer and the new device's own signer must sign **the same** UTF-8 LF-terminated challenge bytes with P-256/SHA-256 IEEE P1363:

```text
Praxis.ReplacementDevice.v1
<installation UUID>
<enrollment UUID>
<principal UUID>
<recovery-key version, invariant decimal integer>
<replacement P-256 SubjectPublicKeyInfo, standard Base64>
<replacement RSA-3072 SubjectPublicKeyInfo, standard Base64>
<replacement signing-key's wrapping-key binding signature, standard Base64>
<expiration, Unix UTC seconds>
<32-byte challenge nonce, standard Base64>
```

The registry atomically consumes a challenge attempt even when either signature is invalid. It also rejects expired, disabled-principal, stale-recovery-version and already-enrolled signing-key cases. Only after verifying both signatures does it enroll the replacement device under the **existing principal**, with the same attested wrapping key and no new ambient membership. All spaces the principal owns or can read are marked `rekey_required` and current key-envelope downloads remain blocked until a legitimate owner publishes a fresh epoch.

**This is not yet rate-limited remote enrollment.** The `Begin` method must not be mapped as an anonymous HTTP endpoint: an actual ingress has to authenticate/account-bind the request, rate-limit creation and signature guessing, bound request bytes/time/concurrency and redact diagnostic metadata (#15/#19/#41).

## Restoring client-held knowledge after the last device is lost

After independently authorizing a replacement device, the owner may fetch the **last historical signed encrypted recovery envelope**, even when the space is fenced for rekey. Only an authenticated **space owner** can fetch it; household members cannot. The client must separately pin the historical owner's signing public key and minimum trusted epoch before verifying its signature, then use the **independently held AES-GCM recovery secret** to decrypt the historical content key. The server never sees this secret or any decrypted key.

The recovered key is for decrypting **historical ciphertext**. Publishing the next epoch rotates the key for future encryption and new authorized recipients; it **does not rewrite all existing encrypted records** or erase ciphertext/plaintext already downloaded by revoked devices. The future #18/#19 data format must preserve each item's encryption epoch and explicitly re-encrypt/revoke content as its security policy requires. An online client cannot assume a recovery envelope is trustworthy just because the host returns a valid-looking record.

**Account recovery is not equivalent to automatic household trust re-establishment.** Recipients must independently approve/pin replacement owner devices or verify a durable owner-signed recovery delegation before accepting a fresh owner signature. The current registry does **not** implement that ceremony; it must be completed and negatively tested before #53 can close or private data sync can be switched on.

## Loss and faults

- All normal devices lost, valid independent recovery signer available: **new enrollment can be authorized**.
- Content recovery secret and valid signed historical envelope available: **old content key can be decrypted locally**, subject to the client's pinned signer and epoch.
- Recovery signer lost but an ordinary owner device is still valid: the ordinary device remains usable, but replacing the registered recovery signer requires the old recovery key under v1; do **not** claim it is automatically restorable.
- Every normal device and the independent recovery signing private key lost: no authenticated new enrollment through this path.
- Every valid content decryption key and independent content recovery secret/envelope lost: ciphertext is **irrecoverable**; there is no admin/master-key bypass.
- Expired/replayed/failed challenges, disabled principal, foreign installation, mismatched public keys, stale recovery version and incomplete rotation must deny enrollment without mutating grants or disclosing content.

This component deliberately does **not** write an OS keychain, expose an authenticated enrollment API, configure TLS/OIDC issuer verification, or create a network session. Those remain #15 release gates; #19 owns the networked private-data sync transport, #40 owns coordinated encrypted backup/restore, and #41 owns deployed secret custody and operational authorization.

## Reproducible tests

`tests/Praxis.Storage.Tests/Security/RecoveryEnrollmentTests.cs` exercises valid two-signer enrollment across process restart, historical recovery and key rotation, wrong-key consumption/replay, household-member refusal, recovery-key rotation invalidating pending requests, disabled principal, bounded expiry and duplicate-signing-key refusal. Existing `SecurityRegistryMigrationTests` must pass migration from historical schema v1 to v3.

```sh
dotnet restore Praxis.slnx --locked-mode
dotnet test tests/Praxis.Storage.Tests/Praxis.Storage.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Security
dotnet test Praxis.slnx -c Release --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
python3 -m unittest discover -s scripts/github -p 'test_*.py'
```

Passing synthetic registry tests proves only the described library-level contract. The feature still requires a real, self-hostable issuer and device ceremony, independent client key-pinning, principal/session source authorization at actual data execution, two persisted client records and required negative security cases before #15/#53 can be closed.
