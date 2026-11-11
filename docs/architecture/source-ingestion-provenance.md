# Canonical source intake and provenance

**Scope:** Local source ingestion contract for #27 and parent feature #59. This describes existing capture routes and their durable evidence. It does not authorize future integrations or create a general provider framework.

## Source and capture records

| Owner | Stored evidence and meaning |
| --- | --- |
| `sources` | Stable `source_id`, origin kind (`file` for implemented local sources), canonical locator, creation UTC and active/revoked status |
| `items` | Stable `item_id` belonging to the logical source; a shared content digest never defines source identity |
| `item_versions` / `original_objects` | Source revision, observation UTC, original SHA-256 and byte length; stored bytes are verified separately |
| `ingest_requests` | Durable request key and committed source revision; repeating a committed request returns that revision rather than capturing newer bytes |
| `capture_request_context` (schema v15) | Trusted entry-point route, asserted authority basis, checked path scope and recorded UTC, keyed to the committed request; created in the same transaction as the receipt |
| `processing_jobs` | Per-revision processor, queued/running/completed/unsupported/failed/canceled state, attempt count, lease and failure code |
| `text_derivatives` | Bounded extracted text or unsupported reason, tied to the original digest and processing version |

`LocalSources.ReadProvenance(sourceId, revision?)` projects these records as a read-only source/revision envelope. It includes source identity, status and locator, observed original digest/size, source-text processor state and failure, extraction outcome, retry eligibility, and capture context from the **first committed request for that revision**, when present. When more than one request captures the same unchanged revision, `LocalSources.ListCaptureReceipts` exposes the individual receipts and their separate contexts. `LocalSources.ListProcessing` exposes per-processor state. Historical and revoked metadata remain inspectable only to the trusted local catalog owner; they are not searchable authority.

Version 15 adds context without backfilling or reinterpreting earlier requests. A receipt created under an older schema has a null route, basis, scope and capture timestamp; a replay remains compatible with that historical receipt but does not create consent evidence. `direct_local` denotes use of the lower-level original API and records `unattributed_local_call`, not an approved adapter. New local adapters use:

| Route | Authority basis label | Checked scope |
| --- | --- | --- |
| `selected_file` | `owner_selected` | The selected canonical file path |
| `selected_folder` | `owner_selected` | The selected canonical directory containing each file |
| `watched_folder` | `owner_configured` | The previously configured canonical watched root |
| `quick_note` | `owner_submitted` | The approved private inbox containing the created note |

These labels describe **which trusted local call site supplied the scope**. They are not authenticated user identity, transferable consent tokens or evidence that a remote actor granted access. The scope check happens before original publication. Reuse of an attributed request key with a different route or scope is rejected, even for the same locator. Scope membership is necessary but does not replace the adapters' own path, link and store-exclusion checks.

## Implemented adapter boundaries and lifecycle

- Manual file and bounded folder import (`LocalSourceIntake`) validate absolute paths, canonicalize linked ancestors, reject source symlinks and importing the catalog, and enforce file/folder budgets. A folder batch gives each file a stable request key and records the same selected root as its scope.
- Quick note capture (`LocalQuickCapture`) writes and verifies a private Markdown file in the configured inbox, using a stable capture ID. A conflicting note with the same ID cannot overwrite the committed original.
- Watched intake (`LocalWatchedIntake`) uses an approved root, stable-write checks and periodic rescans; file contents are never treated as authority or instructions. Missing paths preserve already captured originals.
- The routes converge on `LocalOriginals.CaptureFileAsync`, `ingest_requests`, versioned originals and `processing_jobs`. Identical bytes from two locators may share the object store but not source identity, ownership, search scope or revocation.

Changed content under an active canonical locator creates the next source/item revision. Unchanged bytes with a different request key retain the same revision and gain an additional attributed receipt. An old committed request key replays its own revision, including after the source changes; a new request key cannot silently inherit a different route or scope. On a new revision, only the current active revision can be queued, claimed or retried, and stale leases cannot successfully finish or defer work into the active pipeline. Source-backed search excludes old revisions as soon as the new one is recorded. Explicit revocation blocks implicit recapture and suppresses derived search visibility without deleting other sources' content-addressed originals.

The projection's digest and size are catalog claims, not cryptographic proof that storage is intact: use `LocalOriginals.VerifyAsync` to validate the original bytes. `source-text-v1` documents a single processor, not completion of future semantic, audio or image processing.

## Security and authorization boundary

Local capture authority comes from the trusted owner-local call and checked path scope, **not from imported bytes**. Notes, file contents, URLs or embedded prompts never grant filesystem access, connector privileges or outbound requests. No general grant identifier, actor signature, consent proof or multi-device trust model is stored by this schema. URL, voice, browser, GitHub, banking and other providers must implement their own authorization and independently persist the appropriate durable provenance before being treated as active adapters. Reserved source-kind names do not mean those providers exist. Remote identity and permission control belong to #13, #15, #32, #117 and the owning adapter work.

## Conformance and validation

`SourceProvenanceConformanceTests` validates file, quick-note and watched-folder intake against the same catalog: independent source/item identities with shared original bytes, durable captured-route attribution, replay, revision invalidation, separate search ownership, revocation, index rebuild, restart and exact-byte verification, as well as malformed and unsupported input.

`CaptureRequestContractTests` covers selected-folder scope on a batch and repeat imports, conflicting route/scope reuse, out-of-scope watcher claims, current-revision retry/claim restrictions, stale-lease completion/defer fencing, failed extraction/recovery and missing watched-file preservation. `SqliteCatalogTests` covers schema-v15 upgrade and the absence of invented consent evidence on legacy receipts.

From the repository root:

```bash
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test tests/Praxis.Infrastructure.Tests/Praxis.Infrastructure.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~CaptureRequestContractTests|FullyQualifiedName~SourceProvenanceConformanceTests"
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
```

**Remaining issue #27 acceptance:** the current conformance suite proves the shared contract for three active *local* adapters only. Authorization-specific tests for URL, voice, browser and later connectors must be added when those adapters are implemented by their owning issues; this PR does not fabricate or implement them. Full .NET build/test/format results, CI and owner-machine recovery verification must be recorded before accepting this PR. Cross-provider acceptance and remote ownership are separate gates. Do not close #27 merely because the local receipt schema and projection exist.
