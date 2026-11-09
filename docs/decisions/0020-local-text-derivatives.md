# ADR 0020: Verified owner-local text derivatives

**Status:** Implemented in the S3a proposal under #103; pending verified CI and review. PDF extraction, search indexing and the first-party application are not included.

## Context

The local catalog preserves immutable original bytes, source identity and processing leases but has no trusted text output to rebuild search from. A completed processing job alone does not prove that a text derivative exists. Source revocation must block derived reads without deleting objects shared by independent sources.

## Decision

The additive SQLite v4 migration creates `text_derivatives`, keyed by immutable item revision and processor. Each row holds its source SHA-256 digest, terminal outcome, bounded text or a non-sensitive unsupported reason, and a UTC timestamp. The result is committed in the same transaction that clears the processing lease and changes the job state. The lease token, deadline and active source are rechecked before writing. Failed decoding and original-integrity errors mark the processing job failed without publishing a derivative. Repeated claims cannot overwrite an existing output.

The first `source-text-v1` processor runs only when explicitly invoked by a trusted owner-local library caller. Its claim is restricted to that processor key; other queued processors remain untouched. It accepts only `.txt`, `.md` and `.markdown` file sources, with strict UTF-8 decoding, optional UTF-8 BOM removal, common embedded control-character rejection, a PDF signature mismatch check, and a **1 MiB maximum input and output**. For supported extensions, it reads the immutable original object, checks its length and SHA-256 digest before decoding, then records the accepted text. No imported content is executed or used to resolve filesystem paths or start network requests.

Unrecognized files and PDFs receive an explicit `unsupported` outcome and reason, not fabricated extracted text. A text candidate with a conflicting PDF signature or binary control characters is unsupported. Invalid UTF-8 or a corrupt original produces a bounded failure code. Originals remain unchanged and independently verifiable. The first revision of this decision intentionally does not include PDF parsing; that is still a separate #103 acceptance criterion subject to parser review and malicious-document/resource-limit tests.

The derivative is part of the private SQLite catalog and coherent snapshot. An authenticated library read for a specific item revision only returns a result when its source is still active, the recorded digest matches the immutable revision, and the job's terminal state matches the output. Revocation makes even historical derivatives unreadable through this API; physical erasure and search index/cache cleanup require separate lifecycle acceptance. Future retrieval must apply the same source grant filters to every query and cache, not rely on knowing an item ID.

## Failure and compatibility behavior

- Catalogs at v1, v2 or v3 upgrade transactionally to v4. Historical original objects, source IDs, revisions, request receipts and processing jobs are retained. v2/v3 snapshots remain verifiable and restore into a staging catalog before upgrade. New snapshots include v4 text results in SQLite.
- Parsing is memory-bounded by the 1 MiB original limit. Source-I/O, permission, digest and decoding failures are stored as non-sensitive codes; raw paths and exception messages are not written to processing status. Cancellation leaves a leased job recoverable after expiry instead of claiming a successful import.
- An expired, canceled or revoked lease cannot commit a new text result. The SQLite transaction prevents publishing text without its matching terminal status, even across crashes. No background scheduler, remote endpoint, automatic retry loop or network egress is introduced.
- Persisted text contains potentially sensitive user content. The private local-store permissions and backup requirements apply to the entire catalog. This is **not encryption at rest**, secure deletion, protection against same-user malware, or an authorization decision for HTTP/browser/MCP callers.

## Remaining gates

#103 still requires a separately reviewed bounded PDF extractor, broader signature/MIME handling, derivative reprocessing and deletion policy. #102 owns worker concurrency/cancellation budgets and complete source lifecycle, #106 owns rebuildable index/search behavior, #108 the owner-facing Inbox, and #130 integrated first-run acceptance. Real-device backup tests remain open under #128. Neither these requirements nor CI success are implied by the present software slice.
