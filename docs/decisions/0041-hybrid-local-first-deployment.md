# ADR 0041: Hybrid local-first deployment and independent inference placement

**Status:** Accepted product/deployment direction (owner decision, November 11, 2026). This ADR selects the **order and security boundaries** of deployment classes; it does **not** certify a build, approve infrastructure spending, expose a private endpoint, or waive release evidence.

**Supersedes:** no existing security ADR. Refines the deployment ordering in [ADR 0003](0003-processing-trust-boundaries.md), [ADR 0021](0021-owner-local-caller-security.md), [ADR 0038](0038-private-device-certificate-identity.md), and the host composition boundaries in [ADR 0039](0039-layered-architecture.md). Implements the product intent recorded in [#50](https://github.com/berniemackie97/Praxis/issues/50).

## Problem and goals

Praxis is a permissioned, source-grounded personal knowledge and agent platform, not a permanently disconnected file browser or a mandatory cloud service. It must remain useful with no network and no provider subscription, yet support opt-in remote inference, connectors and ultimately continuously available private agents and clients. Choosing a hosting topology must not implicitly change where any source is stored, processed or disclosed.

The decision separates four independent choices: **application host** (desktop, private server, optional remote host), **storage/processing security space** (local-only, client-held encrypted, trusted-host readable), **model/agent execution location** (local or approved external service), and **client connection** (protected first-party pipes, independently authenticated loopback integrations, authenticated private-host transport). No choice silently authorizes another.

## Decision: local default, optional private host

1. **First certifiable class — owner-local macOS desktop (D0).** One interactive native SwiftUI app supervises the owner-user .NET 10 worker through inherited stdin/stdout pipes. Canonical SQLite/original data stays under owner-private local paths. No source-bearing HTTP service, public ingress, always-on daemon, mandatory cloud account or subscription. The separate loopback health-only API exposes no source-bearing routes and is not authorization evidence. Preserve offline capture, indexed search, supported local inference, source provenance and recovery. A release is **not certified** until #41's remaining D0 evidence, #130/#215 hardware/packaging and #128/#46 relevant recovery/release gates pass.
2. **Next optional class — owner-managed private host (D1).** Deploy separately on an owner-selected, continuously powered machine and permit explicitly enrolled clients on an expressly configured private network (e.g. a WireGuard-compatible device VPN). A VPN is not an application identity or source grant. Each source-bearing network operation still needs independently authenticated TLS/mTLS, enrolled-device proof, current per-space/source permissions, replay protection, bounded requests, recovery and audit. Do not mandate a VPN vendor, expose a public port by default or require D1 for a working desktop.
3. **Later optional class — user-chosen remotely hosted installation (D2).** Reuse the D1 authorization/domain contract when appropriate, with separately certified host/provider, network policy, secrets, quotas, costs, encrypted backup and incident/upgrade operations. Managed SaaS or public multi-tenant hosting is a possible later product, **not** part of D0/D1 certification.
4. **The core stays modular, not distributed by default.** Domain/Application use cases remain host-neutral per ADR 0039. Reuse appropriate Infrastructure adapters behind ports; transport and runtime composition remain host-specific. Do not convert the trusted SwiftUI/worker pipe into a private HTTP dependency for architectural uniformity. Do not share a live SQLite WAL database across computers or let multiple processes own the same local catalog. Synchronization (#19) is a separate authenticated versioned protocol with offline conflict/receipt semantics, not copied database files.
5. **Default is no private network exposure.** D1/D2 require explicit approval for hardware, operator, credentials, ingress/egress and spending. A macOS laptop can prototype a host but is not presumed to meet always-on uptime; real deployment capacity must be measured.

## Model and agent placement is independent of hosting

**Offline-capable is not model-local-only or air-gapped.** A D0 installation may invoke approved remote LLMs/agents while its authoritative catalog remains local. It may also use local Ollama/Qwen or later approved on-device engines. Existing approved Codex/Claude subscription-CLI verification ([ADR 0034](0034-subscription-verification-ladder.md)) is one *bounded* integration route, not a blanket general agent authority. Potential OpenAI, Anthropic, DeepSeek, Grok, Gemini and other provider integrations must satisfy provider terms and an implemented, tested adapter; subscriptions do not imply paid API access or transferable plan credentials.

| Execution choice | Network required | Private source disclosure |
| --- | --- | --- |
| Local parsing/retrieval and supported on-device models | No | None beyond the approved local app |
| Remote LLM/CLI verification from D0 | Yes | Only an inspectable, task-scoped, explicitly approved payload allowed by the source security space and provider policy |
| Remote agent/tool execution | Usually | Separate approval for provider disclosure **and** each tool/resource/action; model output never becomes a permission grant |
| D1/D2 trusted-host inference | Yes between devices when remote | Only sources deliberately classified and granted for trusted-host processing; new provider egress needs its own consent |

**Privacy invariant:** ADR 0003's *local-only security space* forbids remote upload of original, derivative, index and associated metadata; merely clicking a one-time remote model button cannot bypass that classification. A user can ask an external model a new standalone prompt without sending restricted content. If source-grounded remote processing is desired, require the independently approved processing-class/export migration and scope/retention review before disclosure. Client-held encrypted spaces can synchronize ciphertext but must not be decrypted or indexed by the host. Trusted-host readable spaces can enable unattended agents only under current source/operation grants. Every secondary provider is an independent disclosure boundary, even if the host already knows the plaintext.

**Routing and operations:** local-first is a default preference, not an enforced ban on remote models. Owners select enabled providers, approval and redaction policy, budgets, limits, retry/degradation and whether automatic escalation is allowed **within an explicitly approved policy**. A rejected, offline, exhausted or unauthorized remote provider must not corrupt canonical facts, silently switch provider or disable local capture/search. Preserve model identity, permitted disclosure metadata, consent and cited evidence without storing credentials or unredacted provider error responses. Remote work that can execute tools requires revocable tool-specific permission and audit beyond model completion.

## Rollout and release acceptance

| Phase | Product outcome | Existing owners of work | Exit evidence, not assumed present |
| --- | --- | --- | --- |
| D0 foundation | Native macOS local app, private-pipe worker, owner-private original store, offline recovery | #41, #130, #215, #128, #46 | Actual packaged app first-run, denied/other-user and volume faults, measured resources, restore/rollback and signed distribution appropriate to claimed release |
| D0 intelligent MVP | Source-grounded assistant, approved agent actions, usable local models and optional explicitly approved remote models | #170, #31, #32, #33, #172 and cross-domain MVP issues | Real mixed-domain evidence, consent/egress/quotas, provider-offline behavior and approved-tool denial tests |
| D0 external integrations | Separately paired, scoped and revocable **localhost** callers only if enabled | #107, #117, #112, ADR 0021 | Real credential and origin/Host/CSRF denial tests; no implicit browser, LAN or household access |
| D1 private host | Owner-managed private-network service and optional sync/always-on agents | #19, #15, #65, #46 | Approved host, actual mTLS, authorization, backup/key rotation, real restart/overload, measured limits and clients; no assumed VPN trust |
| D2 optional hosted | User-selected remote/cloud hosting | #65, #19 plus future implementation as justified by evidence | Separate host/provider, tenancy, cost, incident response, key custody and recovery certification |

M0–M7 remain **capability milestones**, not chronological gates forcing cloud infrastructure before a usable local product. D0 foundation is not the complete intelligence MVP (#170). No hardware procurement, cloud service, vendor, RPO/RTO or target capacity is approved by this ADR. #137's queued CI is not executed cross-platform evidence.

## Alternatives and tradeoffs

- **Pure desktop forever:** smallest attack surface/cost, but prevents offline-device access, independent unattended automation and future sharing/sync without substantial downstream changes.
- **Mac as always-on server:** reuses hardware for prototypes, but laptop sleep, mobility, power and removable storage undermine availability; do not certify uptime by assumption.
- **Private host first:** achieves remote mobile clients/24-hour jobs earlier, but stalls the validated local MVP on authentication, operation, hardware and disaster-recovery work.
- **Cloud/SaaS first:** simplifies remote reach but requires provider costs, tenant isolation, incident operations and sensitive-data custody before the local product's value has been demonstrated.
- **Selected hybrid:** slightly more up-front contract discipline but preserves optional hosted execution without duplicating domain logic or imposing network dependencies on offline use.

## Operational tracking decision

**#41 is the D0 owner-local deployment/observability ticket**, with done checks only for already verified software behaviors and explicit remaining installation, real-device, retention/capacity and rollback evidence. Remote-host authorization, credential rotation, encrypted off-device recovery and same-host operations remain **uncompleted** at parent **#65** and the implementing #19/#46/#107 gates; they are not marked tested or made prerequisite to offline D0 use. Closing #41 later must not close #65 or imply D1/D2 certification. The [versioned delivery plan](../../scripts/github/roadmap.json) places #41 in foundation lane L0 while retaining #65 under M7. Project #3 fields need live reconciliation when project edit access is available.

This ADR is **accepted policy**; its implementation and release gates are not automatically accepted.
