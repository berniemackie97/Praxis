# ADR 0003: Explicit content-processing trust boundaries

**Status:** Accepted security baseline; remote-processing mode deferred to [#50](https://github.com/berniemackie97/Praxis/issues/50)

## Context

Praxis must synchronize private notes while later supporting source-grounded retrieval and automated workflows. The same service cannot both be unable to decrypt private data and independently index its plaintext without some separately authorized decryption boundary.

## Decision

All capabilities that expose private content or perform external actions are disabled until the required authorization, processing, transport and recovery policy is configured and tested. Local editing does not imply approval to upload or process data remotely.

- Client content must not leave the device by default.
- Public or remotely accessible private-data endpoints must not be enabled before authentication, trust-mode and recovery gates are met.
- Each collection's processing permissions must be explicit. External inference and connector disclosure require distinct permission, even when trusted-server synchronization is selected.
- Search-derived data inherits the source's access restrictions and deletion rules.
- A model cannot grant its own tool or content access. Enforce permissions in the execution layer.
- Existing `/health/live` is limited to process liveness and must never return private state.

## Deployment alternatives pending decision

**Trusted processing service:** service decrypts authorized data to synchronize, index and execute work. Operationally straightforward for server-side capabilities, but *not end-to-end encrypted*. Server and host security become critical.

**Client-held encrypted collections:** keys and plaintext stay on clients, server stores ciphertext. Preserves privacy against a compromised storage service but complicates key recovery, search and unattended server-side workflows.

**Hybrid:** may support distinct collection-level privacy classes later, at the price of stricter lifecycle, key and cross-mode validation. Do not create placeholder mode flags or partially implemented crypto.

Selecting the deployment mode, authorized hosts, key custody and recovery approach belongs to [#50](https://github.com/berniemackie97/Praxis/issues/50). This ADR makes **no claim** that remote sync or encryption has been implemented.

## Consequences

- Identity and record schemas cannot assume unrestricted server plaintext.
- Remote sync work must wait on the owner-selected mode and tested key/recovery design.
- Future server-side retrieval is allowed only within explicitly authorized trust boundaries.
- There is no hidden fallback from ciphertext mode to plaintext processing.

See [threat model](../security/threat-model.md) for the threat register, owners and verification gates.
