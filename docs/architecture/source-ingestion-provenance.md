# Canonical source intake and provenance

**Scope:** Local ingestion contract for #27 (feature #59). This document describes the implemented catalog and capture entry points; it is not authorization for additional provider types or a new ingestion framework.

## Canonical records

| Owner | Stored evidence and meaning |
| --- | --- |
| `sources` | Stable `source_id`, kind (`file` for currently supported local file paths), canonical locator, created UTC, active/revoked status |
| `items` | One stable `item_id` linked to the logical source; content-addressed originals do not define source identity |
| `item_versions` and `original_objects` | Per-item revision, observed UTC, original digest and byte length; exact original bytes remain independently verified in the object store |
| `ingest_requests` | Durable request-key receipt, source, committed revision and state. Replaying a committed request returns its previous revision rather than silently importing changed bytes |
| `processing_jobs` | Versioned `processor_key`, queued/running/completed/unsupported/failed/canceled state, retries, leases and failure reason |
| `text_derivatives` | Bounded content or explicit unsupported reason, scoped to source revision, digest and processor |

`LocalSources.ReadProvenance(sourceId, revision?)` joins these canonical rows into a single **read-only** `LocalSourceProvenance` envelope with source/item identity, source kind/locator/status, source creation and version observation time, digest and byte count, text processor identity, processing state/failure, and extraction outcome/reason. The default revision is the latest. Explicit historical revisions remain inspectable by the trusted owner, including when a source is revoked; that is **not** permission to return their content in search or to a remote caller.

The projected digest/size describe recorded catalog claims. Use `LocalOriginals.VerifyAsync` to verify stored bytes. The text processor is specifically `source-text-v1`; its state does not claim that all future semantic or media processors completed. There is no implied semantic understanding of unsupported types.

## Implemented capture boundaries

- Explicit file and bounded folder import (`LocalSourceIntake`) validate owner-selected fully-qualified source paths, canonicalize linked ancestors, reject source symlinks and importing the catalog, and use per-source idempotency keys.
- Quick note capture (`LocalQuickCapture`) explicitly creates and verifies a private Markdown file in an approved inbox; the stable capture ID is part of its receipt. A conflicting same-ID note cannot overwrite the previous original.
- Watched intake (`LocalWatchedIntake`) processes an owner-approved folder after stable-write checks and rescan; it reuses the same source capture and original publication code, without treating filesystem event contents as instructions.
- These paths converge on `LocalOriginals.CaptureFileAsync`, `ingest_requests`, `item_versions` and `processing_jobs`. An identical content digest on different locators may share stored bytes, **never source identity, revocation, search authority or review state**.

On a new request key, changed content at the same active canonical locator creates another revision under the same source/item. Replaying an old committed key returns its original receipt, not the latest source version; adapter-specific checks still enforce the configured current byte limit. Missing watched paths preserve already captured originals. Explicit revocation prevents implicit recapture and invalidates current derived search visibility; historical metadata/originals are retained according to existing policy. Only the permitted current active revision participates in source-backed search.

**Authority boundary:** current local capture is authorized by the trusted owner-local invocation and selected file/folder/inbox boundary, not by source bytes or URL strings pasted into notes. The catalog currently does **not** persist a general grant token, a durable per-request actor identity, a proof of consent, or an adapter-origin enum; **do not invent these fields when showing provenance**. Any future URL fetch, voice recording, browser extension, GitHub or banking connector must independently define and verify scoped authorization and add its own durable authority/provenance records before intake. Merely having a `source_kind` value reserved in the schema does not make that adapter implemented. Remote identity, consent and tool controls belong to #13/#15/#32/#117 and corresponding adapter tickets.

## Conformance and acceptance

`SourceProvenanceConformanceTests` exercises three existing entry points in one catalog: explicit file import, quick capture and watched-folder capture. It verifies separate source/item identities for identical original bytes; durable receipts; processor identity and outcomes; a new revision hiding earlier search hits immediately; request-key collisions; source revocation and rejected replay; derived-index rebuild; byte-exact originals and restart recovery. A separate test checks unsupported extension vs malformed UTF-8 as distinguishable outcomes, and another checks idempotent retry after restart against a changed source.

Run from the repository root:

```bash
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test tests/Praxis.Storage.Tests/Praxis.Storage.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~SourceProvenanceConformanceTests
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
```

**Open #27 gates:** production-grade durable authority/consent provenance for new adapter types; source-kind-specific contract tests when real URL/browser/voice connectors exist; recovery/failure matrix across those adapters; local execution of all tests and owner-machine failure/restart verification. The independent #29 retrieval evaluation and #130 first-use acceptance remain separate. GitHub Actions #137 is blocked by the absence of eligible runners; a queued run is not verification. This slice does not justify closing #27.
