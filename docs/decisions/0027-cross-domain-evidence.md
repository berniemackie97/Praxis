# ADR 0027 — Typed local financial evidence and version-specific timed media cues

**Status:** Proposed; implementation and real-device verification pending. Tracks #181/#184; #177/#178 remain separate product acceptance.

## Context and constraints

Praxis must work across technical files, timecoded media and owner-authorized financial records, without introducing an unrelated database, hidden model calls or a bank login requirement. Existing catalog identities, immutable originals, digest-verified reads, current-source fences, derived text and FTS5 are the foundation.

## Initial video/caption evidence contract

A `.vtt` or `.srt` sidecar is preserved and decoded strictly as bounded UTF-8 text under `source-text-v1`, then becomes searchable in existing FTS5. A read-only media query uses FTS to select at most 40 caption documents, parses cues with a 2,000-cue, 5,000-line, 700-character-per-cue, 120-second-per-cue limit, and only returns a timestamp when **every query term appears within that one cue**. This is not semantic question answering.

The media link must be explicitly digest-bound by the caption filename: `<full-media-filename>.<full-video-SHA256>.vtt` or `.srt`, e.g. `demo.mp4.<64 lowercase hex>.vtt`, alongside the captured `demo.mp4`. It must match a single active `.mp4`, `.mov`, `.m4v`, `.webm` or `.mkv` original currently carrying exactly that digest. The filename is an owner/provenance binding, **not proof that the captions accurately describe the video**. A next-stage approval/review flow must validate associations. Plain `demo.vtt` or `demo.mp4.vtt` is still preserved/searchable but cannot silently bind to a video; no guessing when captions/paths are ambiguous.

The returned evidence includes source IDs, item IDs, revisions, digests, original media locator, cue text, UTF-16 character span, exact millisecond range and processor identity. It is a **read-only locator**, not permission to open or play arbitrary media. #177 must verify original bytes and independent access again when opening/exporting the clip; do not retain access after revocation. Malformed timed cues abstain rather than supply a fabricated timestamp. No video is decoded by this pass; neither unsupported codec playback nor silent remote transcription is claimed.

## Initial local financial evidence contract

A trusted first-party `budgetSummary` request explicitly names one already-captured **active CSV source ID**, year and month. The analyzer reads only that current original from the same immutable object store, verifies SHA-256, strictly decodes at most 1 MiB UTF-8 (optional BOM), and refuses a changed or revoked source before returning.

Initial CSV profile has exactly these columns and order:

```csv
date,record_id,kind,amount,currency,category,description,status
2026-11-01,pay-nov,income,4500.00,USD,Income,Salary,posted
2026-11-02,rent-nov,expense,1350.00,USD,Housing,Rent,posted
2026-11-03,utility-dec,bill,140.00,USD,Utilities,Upcoming utility bill,estimated
2026-11-04,groceries-1,expense,135.00,USD,Groceries,"Food, first week",posted
```

Required:
- Date = date-only `yyyy-MM-dd` in the export's calendar (no inferred timezone or FX); currency = USD (no silent conversions).
- `kind` = `income`, `expense`, `bill`, `refund`, or `transfer`; all entered amounts are positive decimals with at most two fraction digits and a bounded magnitude. `status` = `posted`, `pending`, or `estimated`; only `bill` is `estimated`.
- Unique `record_id` within the selected snapshot. Byte-for-byte equivalent typed repeats are skipped; conflicting IDs fail the entire report. Transfers do not count as spending; refunds subtract from spent totals. Pending amounts are shown separately, not silently booked. `bill/estimated` is an **unpaid planned obligation**, separate from already posted expenses. If the same liability appears both as planned bill and posted transaction with unrelated IDs, the owner must reconcile it before relying on the net estimate—no model quietly guesses.
- At most 5,000 rows, 100 returned source-row citations, 2,000-character parser field cap with stricter per-column typed-value limits and strict quotation/line limits. Source revision/digest and a truncated-evidence flag accompany the result.

`actualBalance = postedIncome - postedSpending`. `balanceAfterPlannedBills = actualBalance - plannedBills`. No recurring-bill inference, hypothetical cuts, household coverage claim, financial write or tax/investment advice is provided by this deterministic baseline. #178 will add reviewed scenario budgets, transparent assumptions, grounded recommendation and approved output. #162 remains research for **optional** bank API connectivity; this local-import workflow requires none.

## Trust, limitations and acceptance

Only the already trusted first-party private stdio worker exposes these operations. No new HTTP listener, model API or bank connector. Originals are never modified; an attached source's status/revision is rechecked for each operation. The media search limit can miss results outside its bounded 40-document candidate window, and no subtitle/video semantic alignment is independently verified. The CSV profile covers a manually consolidated local monthly dataset, not arbitrary bank exports or multiple accounts.

Verify exact parser behavior, digest fences, malformed documents, duplicate/conflicting finance IDs, revisions/revocations, restarts and source evidence in .NET tests. Run the full matrix and actual Mac UI smoke before accepting or merging this slice. Do not mark #170 complete until first-party clip playback, budget suggestions and the broader intelligence/tool workflow are proven.


## Owner-controlled monthly spending scenario (read-only)

The supported financial CSV path can now produce a **hypothetical** category-target comparison from the same current active original, not mutate historical transactions. An owner supplies zero to 50 distinct categories and decimal USD target amounts. Every target must refer to a category with positive **posted** spending and be between zero and the observed spending for that category. Unknown categories, duplicates, fractions beyond cents, negative or greater-than-observed targets fail closed. Untargeted categories and already estimated unpaid bills are unchanged.

For each explicit target, `illustrativeReduction = observedPostedSpending - hypotheticalTarget`; the scenario-wide reduction is the sum of those differences. `hypotheticalNetAfterPlannedBills = baselineNetAfterPlannedBills + illustrativeReduction`. These calculations describe **what a comparable month could look like under owner-entered targets**, not a retroactive change to this month's actuals, an assertion that spending was unnecessary, or a prediction of achievable savings. Pending transactions, incomplete accounts, unknown recurring obligations and the lack of an opening cash balance remain limitations; the output is not a financial plan until the owner reviews assumptions and coverage.

The private request carries source identity, year/month and bounded amount strings. Backend calculations use decimal, independently re-read the verified original, and recheck the active source/revision/digest before returning. UI results are cleared when the selected source, observed revision, month/year or target changes. No scenario is saved, sent to a model, exported, used for financial action, or treated as canonical knowledge. #178 remains open for a cited editable budget, actual recommendations based on owner priorities, independently approved writes and complete source coverage. The #170 MVP gate cannot pass on this baseline.
