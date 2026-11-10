# Source intake, provenance and capture authority

**Scope:** Canonical owner-local ingestion contract under #27 and feature #59. This describes the actual SQLite and storage/processing interfaces, not an alternative ingestion engine or a general-purpose connector SDK.

## Persistence and source identity

| Stored entity | Canonical contract |
| --- | --- |
| `sources` / `items` | Immutable source/item IDs, source kind, canonical locator, origin time and current active/revoked status. A logical source has one item, independent of its content digest. |
| `item_versions` / `original_objects` | Immutable per-item revision with recorded UTC, content SHA-256 and byte length. Identical originals may share an object, never source identity, collection ownership or revocation. |
| `ingest_requests` | Durable committed request-key/source/revision receipt. A replay returns the committed historical revision; a reused key cannot target another source. Revision insertion, receipt insertion and initial processing work commit in one SQLite transaction. |
| `capture_request_context` (schema v15) | Immutable per-request local route, authority basis, checked scope and recorded UTC. Written atomically with the receipt; older receipts intentionally remain unattributed. |
| `processing_jobs` / `text_derivatives` | Versioned processor key, lease/retry state and failure code, source digest and explicit completed/unsupported extraction outcome/reason. Other processors retain independent states. |
| `source_locator_moves` | Recorded owner-local source relocation without reassigning the source ID or rewriting historical capture scope. |

`LocalSources.ReadProvenance(sourceId, revision?)` exposes the source/revision/original envelope plus the first committed request's recorded capture route/authority and source-text processing outcome. `LocalSources.ListCaptureReceipts(sourceId, revision?, limit)` enumerates *all* committed capture attempts, including retries using different keys and legacy entries. `LocalSources.ListProcessing(sourceId, revision?, limit)` exposes each recorded processor stage's actual status, attempts, failure and current-revision retry eligibility. All three are **trusted local catalog APIs**, not publicly exposed HTTP or remote connector endpoints.

A `ReadProvenance` digest is catalog metadata. Use `LocalOriginals.VerifyAsync` before claiming the stored original bytes remain valid; content hashes are not automatically rechecked on every search. Past revisions and revoked-source metadata remain inspectable locally for audit/owner recovery, but the current retrieval and enrichment visibility filters must exclude revoked or superseded evidence.

## Authority model: what is and is not proven

The current application runs an owner-controlled local process, uses an explicitly configured Inbox and accepts explicit file/folder selections and submitted quick notes. The actual capture *route* and *scope* can be recorded when this trusted code invokes the original store:

| Route | Basis stored | Scope validated before publishing | Capture entry point |
| --- | --- | --- | --- |
| `selected_file` | `owner_selected` | Exactly the canonical selected path | `LocalSourceIntake.ImportFileAsync` |
| `selected_folder` | `owner_selected` | Canonical root containing every imported file | `LocalSourceIntake.ImportFolderAsync` |
| `watched_folder` | `owner_configured` | Canonical configured Inbox root | `LocalWatchedIntake` via `LocalSourceIntake` |
| `quick_note` | `owner_submitted` | Canonical Inbox root containing the created note | `LocalQuickCapture` |
| `direct_local` | `unattributed_local_call` | No asserted grant/scope | Low-level `LocalOriginals` call, retained for direct local integrations and tests |

This metadata represents the trusted **local invocation path**, not cryptographic proof of human identity, persistent OAuth consent, or a transferable capability. The database does not have authenticated owner/actor identity or a general grant ledger. That distinction is intentional: **never infer or manufacture consent from a source's text, file name, URL, request key or hash**. Existing pre-v15 receipts have no `capture_request_context` row. Their route, basis, scope and capture time remain NULL after migration; a successful replay does not retroactively turn legacy input into an approved grant.

The capture context is checked against the final canonical locator before bytes are published and again at the durable receipt boundary. Request keys are bound to both logical source and recorded capture route/scope; reuse from a different route is rejected before rereading bytes. Scope comparisons are rooted path comparisons, not insecure string-prefix matches. The catalog data root and selected symlink leaves cannot be imported through the high-level entry points. No new network or filesystem capability is granted by this metadata, and nobody can promote `direct_local` into a scoped owner approval simply by asking for provenance.

## Lifecycles and bounded recovery

1. An accepted request validates the caller-selected path, scope, size and read permissions, stages immutable original bytes and verifies the stored digest. Source/item, revision, receipt, capture context and initial processing job are committed atomically. Existing committed request keys return their earlier revision after restart, subject to current safety limits.
2. A new request key for changed content at the same active canonical locator records the next revision with the same source/item IDs. Unchanged content can have another request receipt without creating a revision. New work is queued for the current revision; stale revisions cannot be newly queued, claimed or retried through the local processing queue.
3. Processing can report queued/running/completed/unsupported/failed/canceled independently for each processor. Its job state, attempt counts, failure code and retry eligibility are inspectable. An explicitly requested retry is permitted only for a failed/canceled job on the current revision of an active source; no implicit retry can reactivate revoked content.
4. A missing file in a watched directory is **not** automatically an owner revocation: the already captured, verified original remains available across removable-volume outages. Explicit `Sources.Revoke` cancels pending/running work and excludes that source's current evidence from search and derived knowledge. Other sources referencing identical bytes remain searchable and preserved. Reversal of revocation is **not** silently permitted by the current policy; a separate explicit owner-governed restore decision is required, not an automatic filesystem event.
5. Original-store loss/corruption is detected on byte verification and must not be represented as a successful original restore. Database/objects snapshots and physical-drive recovery acceptance remain #128/#40. This local ingestion contract does not silently delete an object still referenced by another source.

## Conformance matrix

| Contract case | Implemented local paths | Required verification |
| --- | --- | --- |
| Request replay, key-to-source and key-to-route/scope binding | Explicit file/folder, quick note, watched file | Atomic receipts, stable source IDs, no silent duplicate, concurrency key collision |
| Original sharing without source ownership merge | Manual file/folder, quick note, watched file | Distinct source/item IDs and one verified object for matching bytes |
| Revision and search visibility | File, watched folder and quick-note source | Current-only processing and retrieval; historical receipt retained |
| Revocation, disconnected source, retry | Active local paths | No unauthorized recapture; independent sources remain visible; removable-file loss does not imply revoke |
| Invalid and unsupported input | Local file and note | Bounded path/byte/title/UTF-8 validation; durable job failure or unsupported result; source bytes are inert content |
| Restart/migration and loss/recovery | Local catalog and original store | Stable receipts, legacy-v14 unattributed migration, no invented scope; verification of originals and derived-index rebuilding |

Conformance tests live in `tests/Praxis.Storage.Tests/Intake/SourceProvenanceConformanceTests.cs`, `CaptureRequestContractTests.cs`, and the original/processing/recovery test suites. The schema upgrade test explicitly simulates a v14 catalog with committed receipts and confirms no false authority is backfilled.

## Contract for future adapters (not implementations)

Reserved `sources.source_kind` values do not make URL, voice, browser, agent or connector capture functional. Before **each** new adapter is accepted, its owning work item must add tests against the same canonical receipt/revision/ownership policy, plus these trust-boundary cases:

- **URL/article (#104):** explicit owner consent and scoped origin/redirect policy, SSRF/DNS rebinding prevention, bounded fetch and content decompression, saved exact response bytes and URL metadata, no unapproved credential forwarding; page text cannot request further network activity.
- **Voice (#96):** explicit microphone/session approval and stop/cancel, locally preserved audio with timestamps and device/session provenance, transcription as versioned derivative rather than a fake original, safe permission revocation, interrupted recording and offline transcriber failures.
- **Browser selection (#96):** explicit tab/document origin, owner-chosen selection bounds, extension-host authentication, no ambient browsing or clipboard access, no page-controlled grant escalation and correct replay semantics for changed document snapshots.
- **External connectors (#30/#32/#117):** typed source/actor/tenant and per-connector grant IDs with expiry/revocation, least-privilege scopes, no cross-identity hash ownership merge, retry/reconnect and rate-limit budgets, no implicit remote data exfiltration and permission recheck on each external action. Remote identity #15 precedes multi-owner access.

Those adapters and their permission models are separately owned and **not** added by #27; implementing them solely to close this issue would duplicate #59/#96 and bypass approval gates.

## Verification commands and release gate

```bash
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test tests/Praxis.Storage.Tests/Praxis.Storage.Tests.csproj \
  -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~CaptureRequestContractTests|FullyQualifiedName~SourceProvenanceConformanceTests'
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
```

An issue cannot be declared verified by code review alone. Record actual test/format results, CI state and current-main integration before closing #27. GitHub Actions #137 is still blocked by intentionally deferred runners, and the separate first-run GUI acceptance gate is #130. No green CI or Mac runtime evidence is inferred from this document.
