# ADR 0030: Text segments, claim priority and lease renewal

**Status:** Accepted. Schema v10.

## Context

Model-backed enrichment (#115) needs bounded inputs that fit a local model's context and can be cited precisely, plus the ability to run for minutes without losing its lease or wasting work on content that has since changed. Backfilling an existing library must not delay newly captured sources.

## Decision

- **`text-segments-v1` stage** depends on completed `source-text-v1`. `TextSegmenter` is deterministic: blocks start at Markdown headings (outside fenced code, for `.md`/`.markdown`) and after blank lines; blocks pack into segments of about 1,200 UTF-16 code units, never exceeding 2,000; oversized blocks split at the last line break that fits, else at the limit without separating a surrogate pair. A section boundary starts a new segment once the current one has at least 200 characters. Segments are contiguous and cover the text exactly; text with no non-whitespace yields none; more than 4,096 segments fails the job with `segment_limit`.
- **Storage holds offsets, not copies.** `text_segments` stores ordinal, start, length and the Markdown heading path. `LocalTextSegments.List` returns segment text from the digest-verified derivative, only for the current revision of an active source.
- **Shared lease primitives.** Downstream processors read input through `Text.ReadLeasedInput` and complete through `LocalProcessingQueue.TryCompleteLeased` (lease held, source active, revision digest unchanged, dependents scheduled), so every stage applies the same fences.
- **Claim priority.** `processing_jobs.priority` (≤ 0): new work is 0; backfilled work gets a negative recency rank, so the most recently observed revisions are processed first and new captures always go ahead of backfill. Claims order by priority, then creation time.
- **Lease renewal.** `Processing.Renew` extends a held lease. It returns null when the lease is lost or the source was revoked, and cancels the job when a newer revision exists, so expensive work on superseded content stops at its next renewal.

## Consequences

Segments are reproducible from the derivative and the segmenter version; changing the algorithm means a new processor key. Captions and code are segmented as plain text for now; cue-aligned and symbol-aware segmentation are separate decisions (#177, #153).
