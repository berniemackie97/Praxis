# ADR 0049: Intake as one hand-off between tiers

**Status:** Accepted (lead-architect decision with owner, November 14, 2026). Schema v27. Refines the understand step of [ADR 0044](0044-generic-intake-engine.md); tiers, disclosure and verification rules are unchanged.

## Context

ADR 0044 gave each tier its own isolated job: the local tier only triaged, the strong tier read every document from scratch, and the strongest tier read it again from scratch when the strong answer failed. The tiers did not build on each other, the strong tier paid full output tokens for every document, and the owner's local model sat idle while paid plans did all the reading.

## Decision

Understanding is one case passed up the tiers, span by span:

1. **Local drafts.** For spans up to 6,000 characters the local tier writes a complete draft (summary, topics, entities, relations, records). Code verifies every quote, name, date and amount; only verified items survive. Longer spans skip the draft, because at ~14 tokens/second a local model is slower than it is useful there.
2. **Strong reviews.** The strong tier receives the document and the verified draft with every item numbered, and returns only corrections: ids to remove, items to add, and a better summary if needed. An empty correction approves the draft. Without a draft the strong tier reads the span itself.
3. **Strongest corrects.** Only when the strong result fails verification, the strongest tier receives the strong result and the verification problems and returns corrections in the same form.
4. **Code merges and verifies** every addition exactly like a full answer; repeated items are not counted as additions. A reviewer that answers in the full shape has its items treated as additions.
5. **Provenance.** Kept items take the tier that reviewed them. Each document records its cascade, for example "local drafted 12 → strong kept 9, removed 3, added 4 → 13 verified", shown in the Intake panel.

Supporting changes: files left index-only because no model was configured are understood again once the model setup changes (never in a loop); optional local enrichment is reported apart from intake in the status line; recommended defaults use efficient models at low to medium effort, leaving flagship models a manual choice.

## Consequences

- The strong tier's output — the expensive part — shrinks to corrections on most documents; the strongest tier runs rarely and corrects instead of restarting.
- The owner's local model does useful work on every small document, privately and for free.
- Throughput for small documents is bounded by the local model; large documents go straight to the strong tier.
