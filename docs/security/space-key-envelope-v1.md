# Space-key envelope v1 — encrypted distribution and offline recovery

**Scope:** #15 key-custody implementation, built on [device proof v1](device-proof-v1.md) and [ADR 0003](../decisions/0003-processing-trust-boundaries.md). This is an executable **library/registry contract**, not operational end-to-end encryption or a remotely authenticated sync service. The owner-local catalog remains plaintext and the public API remains liveness-only.

## Trust boundary

The trusted owner client creates a **32-byte random content key** per security-space epoch. The client can give that key to multiple independently enrolled recipients **without transmitting it to the server**. It retains control of its key, its device private signing/wrapping keys and an independently protected **32-byte random recovery secret**. The host stores the key's SHA-256 commitment, signed opaque per-device envelopes, and an AES-GCM-encrypted owner recovery envelope; it has no content keys, recovery secrets or RSA private keys.

A device's P-256 ECDSA public signing identity must authenticate its independent **3072-bit RSA recipient SPKI public key**. The signed public-key binding is the UTF-8 LF-terminated message:

```text
Praxis.DeviceWrapping.v1
<standard Base64 of the device's P-256 SubjectPublicKeyInfo>
<standard Base64 of the device's RSA-3072 SubjectPublicKeyInfo>
```

The signature is ECDSA P-256/SHA-256 in 64-byte IEEE P1363 fixed-field form. Legacy enrolled devices from registry schema v1 keep their identity; they **must explicitly register an attested wrapping key** before receiving encrypted keys. The v2 schema migration preserves principals, device signatures, security-space IDs, grants and prior challenges. The issuer/operator enrollment path is not yet remotely available.

**Recipient authenticity is not provided by the server alone.** Before sharing a client-held space, an owner must independently authenticate/pin the intended recipient's signing key (e.g. a verified exchange during enrollment). A compromised host could otherwise introduce an alternate device signing identity and its corresponding encrypted-key recipient. The `ClientSpaceKeys.Protect` API now **requires an independently maintained device-ID → pinned P-256 signing-public-key map** and rejects an unknown or substituted signing identity *before* wrapping any content key. The app must populate this map from a separately approved device/household enrollment exchange, **never by copying the server-returned recipient list**. The committed negative test inserts an operator-visible but locally unapproved recipient and verifies refusal. Device-side RSA binding verification prevents wrapping-key substitution under a pinned signing key. The future user-visible enrollment and pin-persistence UX (#15) must still establish and test that independent approval across sessions. This does not yet constitute a complete malicious-host E2EE trust ceremony.

## Cryptographic envelopes

For each enrolled **read-authorized** recipient (including owner devices), the owner locally encrypts the exact 32-byte space key to the recipient's independently attested RSA-3072 key using **RSA-OAEP with SHA-256**. The ciphertext is 384 bytes. Each recipient has an independent signature over the UTF-8 LF-terminated message:

```text
Praxis.DeviceSpaceKey.v1
<installation UUID, lowercase 32 hex>
<space UUID, lowercase 32 hex>
<epoch, invariant decimal integer>
<signing owner device UUID, lowercase 32 hex>
<recipient device UUID, lowercase 32 hex>
<SHA-256 space-key commitment, uppercase hexadecimal>
<recipient RSA-OAEP ciphertext, standard Base64>
```

The recovery envelope encrypts the same 32-byte key with **AES-256-GCM**, a fresh randomly generated **12-byte nonce**, a 16-byte authentication tag appended after the 32-byte ciphertext, and the 32-byte owner recovery secret as the AES key. Its associated authenticated data (AAD) is UTF-8 LF-terminated:

```text
Praxis.RecoveryKey.v1
<installation UUID>
<space UUID>
<epoch>
<signing owner device UUID>
<SHA-256 key commitment, uppercase hexadecimal>
```

The owner separately signs this UTF-8 LF-terminated recovery package:

```text
Praxis.RecoveryEnvelope.v1
<installation UUID>
<space UUID>
<epoch>
<signing owner device UUID>
<SHA-256 key commitment>
<random nonce, standard Base64>
<AES-GCM ciphertext followed by tag, standard Base64>
```

Every signature uses the enrolled owner's P-256/SHA-256 key in 64-byte IEEE P1363 format. Receivers must verify a **separately pinned** owner signing key, the signed domain, installation, space, recipient, epoch and commitment; then decrypt and compare SHA-256 of the resulting key to the commitment in constant time. The API requires the caller's **durably recorded minimum trusted epoch**, rejecting any older validly signed key; this library does not itself implement durable client freshness tracking (#18/#19).

## Registry policy and rotation

- The v2 registry stores **no private keys or raw content keys**. A trusted owner/operator provisions and cryptographically attests device public keys. The public-key directory is not independently trusted until the owner has verified the intended other-device identity. The operator-only provisioning API is **not** a browser/remote endpoint.
- Only an enrolled owner with a live signed **space-scoped write proof** may publish a new epoch. The host verifies the owner's signing key, every recipient signature, recovery signature, epoch sequence and that the recipient set **exactly matches all active read-authorized devices**. It atomically records the entire epoch, never a partial distribution or silently skipped epoch. There is a **64-device ceiling** pending measured release sizing.
- Enrolling a device, attaching a recipient key, adding/changing a grant, removing a member, revoking a device or disabling a principal sets `rekey_required`. During this interval `Download` supplies **no current envelopes**. An owner must publish a newly generated content key at the next epoch, excluding revoked devices, before distribution resumes. Repeated regrant cannot revive a prior device proof because the separate grant epoch increments.
- Read-authorized devices receive **only their own** RSA ciphertext. Only the authenticated owner can receive the AES-GCM recovery ciphertext through `Download`; other members cannot list recipient public keys or recovery envelopes. The server cannot retroactively erase ciphertext or plaintext that an offline member already copied, and it cannot prove that encrypted recipients all decrypted the same content key; clients detect signed-key-commitment mismatches.
- Future #19 private content reads/writes **must independently enforce** the same rekey/current-grant fence and source-space authorization in the actual data transaction. Registry APIs and owner-proof checks by themselves are not remote transport security.

## Recovery and irrecoverability

The owner must independently preserve **both** the 32-byte recovery secret and a copy of the signed recovery envelope, plus the pinned owner signing public key and the installation/space/epoch identifiers. These materials let an authorized client recover the same content key **offline** after loss of every enrolled device's RSA private key. A server administrator or account password reset cannot decrypt the envelope. A bad factor, tampered packet, foreign installation or older-than-trusted epoch is rejected.

This is **offline key recovery**, not yet remote **account/device re-enrollment**. If every authorized client private key and the independent recovery secret/envelope are lost, client-held ciphertext is irrecoverable. Recovering and distributing new keys to a replacement device requires a reviewed account-recovery enrollment ceremony (#15) and a working authenticated transport (#19), neither of which this version implements. Never infer an invisible server escrow or zero-data-loss backup guarantee. A server copy of the signed recovery envelope alone does not make it retrievable when all device credentials are lost.

## Evidence and release gates

`SpaceKeyDistributionTests` validates two device keys, read-only sharing, per-member envelope isolation, wrong-key and modified-signature denial, missing recipients, atomic epoch sequencing, revocation/rekey, new device onboarding, and offline recovery after device loss. `SecurityRegistryMigrationTests` verifies opening an existing registry v1 and upgrading it to v2 without changing its enrolled identity or grant scopes. Tests require actual .NET execution and their results must be attached before accepting PR/issue progress.

The [security attack matrix](../testing/remote-trust-security.md) remains authoritative. These tests supply partial evidence for S03–S07 and S10–S11/S23 **only**; real network/OIDC/PKCE, key pinning, backup/restore, two persisted clients and actual crypto-backed sync still have separate unpassed gates under #15/#18/#19/#40/#41. Trusted-host spaces here carry class metadata; this code does **not** install or authorize a host-held decrypting key or unattended host processing. Do not expose a private endpoint on the strength of this registry alone.
