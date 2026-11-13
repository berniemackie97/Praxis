# ADR 0042: Owner-local source disclosure authorization for verification

**Status:** Implementation proposed under #32; exact-head .NET and physical macOS acceptance pending.

## Boundary

The first-party SwiftUI application speaks through inherited pipes to its owner-user worker. This contract does **not** make the desktop protocol an authenticated MCP/HTTP tool runtime, grant another local process access to private sources, or create a permission for arbitrary shell/network actions. ADRs 0021, 0003 and 0041 remain binding. The initial protected operation is external model verification of already reviewed evidence.

## Permission matrix

| Operation | First-party local owner | Unpaired agent, website or MCP caller | Verification model |
| --- | --- | --- | --- |
| Owner-local capture, source preview, indexed search | Existing first-party operations | Not exposed | No tool access |
| Local Ollama verification | Explicitly reviewed payload, enabled loopback provider | Not exposed | Receives reviewed context locally |
| Reclassify one source for remote verification | Separate source-specific confirmation | Denied / no endpoint | No authority |
| External Codex/Claude CLI verification | Requires per-source eligibility, explicit chosen provider, narrowed payload, and expiring request consent | Denied / no endpoint | Receives only consented context |
| Modify, delete or transmit arbitrary sources | No grant created by this feature | Denied | No tool access |
| Revoke disclosure grant | Explicit owner action | Denied / no endpoint | Future results fenced; past transmissions cannot be recalled |

## Decision and enforcement

1. Every existing or newly ingested source defaults to **local-only**. Missing policy rows never imply permission. An owner can separately classify one active source as `remote_verification`; this decision has a monotonic revision and is independent of organizational collections. Returning it to `local_only` invalidates all associated active grants and running leases. Re-enabling it increments the revision and **cannot revive** an older grant.
2. For a remote verifier, an approval requires a second explicit consent for the exact excluded/redacted payload and selected provider ID/configuration revision. Each included proposal's source and every included entity-context source must have active remote verification classification. The catalog snapshots the full source-ID set and each policy revision in `verification_disclosure_sources`. No source ID, revision, digest, policy key or consent token is added to the provider prompt.
3. The request-specific disclosure grant lasts **15 minutes**. Claim, immediate pre-send check and result commit each recheck provider and revision, expiry, owner revocation, every included source classification/policy revision, and ordinary evidence freshness. A missing, mismatched or expired grant denies execution; no fallback provider is selected and no partial verdict is applied. Retries are possible only within the original expiry and the existing provider attempt limit. A running process cannot retract content already sent to a third-party CLI.
4. Catalog migration v20 fails unfinished external approvals predating these grants and fences their running attempts. It leaves completed historical results and already authorized local approvals alone. Owners must create and review a fresh request for external processing.
5. Every approval, denial, dispatch, completion, classification and revocation decision is recorded in the owner-private catalog with `owner-local` actor, opaque request/source IDs where applicable, provider ID, reason and UTC timestamp. The ledger stores no note content, source locator, prompt, CLI stdout, credentials or unbounded error messages. It retains at most **10,000** recent records; a separate deletion/retention UX remains to be built. The first-party owner can inspect the bounded record list. A failed transactional audit write cannot commit the related privileged change.

## Denial and recovery cases

The executable tests must cover an unclassified source, missing consent, an entity assembled from two source contexts where only one is eligible, elapsed consent, policy revision change, source and grant revocation after claim, incorrect client argument types, v19-to-v20 migration, and a local-only verifier unaffected by external restrictions. Owner-facing source classification is an explicit review/confirmation separate from payload approval.

## Scope intentionally not completed by this PR

The D0 source classification is not a cross-device security-space membership registry. A future source/space model must enforce its higher-level local-only and client-held encryption restrictions at every disclosure boundary. #32 remains **OPEN** for generalized first-party/MCP tool grants, separate principal/agent credentials, read/search versus write/delete scopes, full operation idempotency receipts, 30-day audit retention and independent MCP client conformance (#117/#118/#119). No source-bearing external API or arbitrary tool runner is enabled here. Mac application confirmation, actual CLI calls, independent cross-platform CI and permission revocation races still require executable verification.

The row-level source eligibility checks do not authorize any external action outside this one verification operation.
