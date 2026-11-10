# ADR 0038: Self-hosted certificate issuer and enrolled-device transport identity

**Status:** Implemented registry-level certificate admission and offline operator-held PKCS#10 certificate issuance under [#15](https://github.com/berniemackie97/Praxis/issues/15); not a deployed, externally reachable authentication service. Completes neither #15 nor [feature #53](https://github.com/berniemackie97/Praxis/issues/53) until actual TLS, operator approval, recovery and end-to-end authorization tests pass.

## Context

Praxis needs a self-hostable identity path with no mandatory paid cloud identity provider while preserving [ADR 0003's](0003-processing-trust-boundaries.md) separation between owners, household members, devices, security spaces and client-held encrypted data. A device ID or owner-local user login is not proof of a remote principal. Signature-verified device proofs already exist in [device proof v1](../security/device-proof-v1.md); a real remotely reachable transport still needs authenticated peer identity and an account/device enrollment ceremony.

## Decision for initial remote device transport

Use **short-lived RFC 5280 X.509 client certificates**, signed by an **explicitly pinned private certificate authority**, together with the enrolled device's existing **P-256 ECDSA key** and short-lived **signed per-operation authorization proofs**. The PKI can be operated without a paid cloud service using, for example, the open-source [step-ca](https://github.com/smallstep/certificates) project. Praxis does not embed a CA private key, invent a bearer/JWT token protocol or mint client certificates from a network endpoint. A trusted out-of-band issuer registration/approval ceremony must verify intended owner/member/device identity before issuance. Existing registry grants determine authorization; certificate names are never security-space ACLs.

This is an **mTLS device-certificate path, not OAuth/OIDC**; browser authorization-code with PKCE is not applicable to a certificate-only client handshake. If human sign-in is later delegated to a separate OIDC provider, the product must use **authorization-code with PKCE**, validate issuer/audience/expiry/signatures through a maintained standards library, and independently bind the resulting subject to an enrolled device and approved recipient identities. A human password reset must never recover client-held encryption keys. Do not silently substitute OAuth bearer tokens for enrolled-device possession.

## Trust and validation

The new `DeviceCertificateIdentity` library verifier accepts a certificate **only when it comes from an independently authenticated TLS client connection**. It checks:

- A caller-configured **pinned custom root**, not arbitrary system-trusted public CAs or a root supplied by a request field.
- An unbroken X.509 chain with strict verification flags, no remote AIA downloads, explicit **Client Authentication EKU** and **Digital Signature key usage**.
- Present validity and a maximum **7-day** leaf certificate lifetime; the private CA can use shorter leases.
- The leaf's **P-256 SubjectPublicKeyInfo** must exactly equal a non-revoked, previously enrolled Praxis device key. The registry, not the certificate CN/SAN text, binds principal ID and device ID.
- Every signed operation proof must name that same principal and device and remain current with live grants, expiry and revocation before protected execution.

The default verifier uses `X509RevocationMode.Online` for issuer revocation. Tests with synthetic ephemeral CAs use `NoCheck` **only in-process** and still verify registry revocation; a deployment cannot cite those tests as proof of CRL/OCSP availability, production TLS handshake binding, certificate forwarding safety or CA operational recovery. Network entry must configure Kestrel to **require actual client certificates on a dedicated HTTPS listener**, validate the issuer, use safe TLS termination and reject forwarding headers from untrusted proxies. #19/#41 own that deployed integration and issuer/secret operations.

## Enrollment, approval and recovery

A certificate is **not** its own enrollment authorization. It must match a device already enrolled through an authenticated owner approval or independent [recovery enrollment](../security/recovery-enrollment-v1.md). A hostile host's newly inserted device key must never cause a client-held collection key to be wrapped automatically: `ClientSpaceKeys.Protect` requires a **separately pinned recipient signing-public-key map**. The app must persist this trust state securely, display the owner/member/device identity being approved, and refuse changed keys without an explicit review.

Lost-all-device recovery uses a separate P-256 recovery signer for authorized replacement-device enrollment and an independent AES-GCM content recovery secret, *not* the CA's signing key or an account reset. The newly recovered device is fenced by the key epoch until a new owner-approved key is published; existing offline copies cannot be erased.

## Scope and release evidence

- **Implemented library surface:** chained pinned-X.509 certificate inspection, enrolled-device/identity matching, proof/session binding, issuer/client-usage/validity denial cases. An operator holding a P-256 signing CA may issue a **24-hour, client-auth-only certificate** from a verified PKCS#10 request, only when its P-256 key exactly matches a currently authorized owner device or a household device with a signed owner-approval receipt. The CA private key is passed in memory, never persisted by Praxis. No private API route is exposed.
- **Not implemented:** public/private listener separation, actual mTLS handshake, certificate issuance/renewal UX, CA lifecycle and operational audit, protected OIDC human sign-in, or cross-process two-client network sync. Persisted client recipient pins and verified key-epoch floors now exist, but an actual OS-level private-key custody and approved enrollment UI still require release evidence. These require #15/#19/#41 deliverables and real-device tests, not just a passing in-process X509Chain fixture.
- **No MFA claim:** short-lived mTLS and one-use signatures use the **same enrolled P-256 private key** for leaf certificate and operation proof; those two verifications do not constitute independent factors.

Before #15/#53 completion, test enrollment, expired/revoked CA/device, stolen/foreign certificate, wrong signing key, client key trust pinning, disabled account, proof replay, last-device recovery and owner approval across independently persisted clients. Before exposing #19, verify actual TLS and operational CA/CRL/OCSP behavior on the owner's Mac plus the supported host environment.

## Alternatives not selected for the first transport

A first-party OAuth/OIDC token issuer was not selected: it would add account credentials, reset recovery and token/refresh lifecycle dependencies before the basic self-hosted device protocol exists, and OAuth alone would not prove possession of approved client-held keys. A managed proprietary authentication service is not mandatory. A token-based human-login path can be added later only with complete threat analysis, separate identity/decrypting-key rights and explicit scope of authority.
