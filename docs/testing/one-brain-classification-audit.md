# One Brain classification audit — #455

The owner reported 134 “confirmed project anchors” among 3,458 entities after #459. Those are screenshot counts, not a verified breakdown of the private catalog. The old root query counted visible entities whose effective type was `project`. It neither established a project boundary nor checked that an entity organized any other knowledge. Tests that supplied clean project roots could not catch that assumption.

## Where the count came from

- Intake retains source IDs, item revisions, original digests and text derivatives. An enrichment proposal stores its producer, claim, quote, offset, and review state. `eligible_enrichment_proposals` suppresses revoked or superseded evidence. The canonical entity projection links names to IDs and has independent owner rename, override, merge and reject decisions.
- `confirmed_entity_mentions` is a **visibility** rule. It includes owner-approved proposals, verifier-supported proposals, deterministic technical extraction, and strong/strongest/reused intake output. The intake quote validator can locate a verbatim span; that does not establish that it entails the proposed type or predicate. High-tier output can still contain false classifications.
- The effective type can be an owner override, a recorded verifier correction, a scoped deterministic technical type or an inference from eligible mentions. An approved mention can still contribute to an inferred type. A conflicting type vote, a name match, an accepted proposal and a verifier type correction are different evidence. Verify each against its own scope and context.
- Repository maps and intake document categories serve discovery and processing. Neither a detected repository root or build marker, nor a watched folder, filename, heading, or source category alone proves an independent project or a semantic territory.

## Rules for deciding what an entity is

| Proposed role | What to check |
| --- | --- |
| Project | Source context establishes a bounded initiative or independently maintained whole: purpose and scope, plus ownership, maintenance or delivery when available. An explicit owner decision counts as governance. A repo or product name alone does not settle the boundary. |
| Component or artifact | The source identifies a constituent or implementation role. Use the existing vocabulary based on that role: class/struct, function/method, binary module, runtime data asset, document, or technology (for example, a library). A component can also be a separate project if its independent boundary has evidence; do not infer either role from spelling. |
| Concept | The source describes the idea or practice itself. It can recur across domains without becoming a project or a member of every entity that references it. |
| Conflicting, ambiguous or unsupported | Keep as unresolved for review. Check for distinct namesakes, contradictory classifications and wrong relation direction. Do not bulk relabel or merge. |

Keep owner-asserted decisions, accepted/corrected proposals, verifier-supported evidence, deterministic observations, and unverified derived output distinct. The word *supported* describes a verifier verdict; it is not independent proof. Check the recorded corrected type, where present, before interpreting a supported project-tagged proposal. The audit's project mention counts describe the original eligible tags, not independent corroborations or an assessment of corrected types.

## Inspect a permitted corpus

1. Start with a coherent [verified backup and scratch restore](../delivery/macos-workbench.md). Keep the scratch run offline with a separate empty Inbox. A normal worker also runs background processing, so never start a new audit worker against the live catalog or use a live Inbox for this test. Use only the authorized snapshot scope.
2. Through the private desktop transport, request `{"requestId":"audit","action":"graphClassificationAudit","limit":32}` (optionally `sourceId` or `collectionId`). Page via `nextCursor`; restart on a stale cursor. Record the revision, count, classification origins and counts of accepted, verifier-supported, technical, and unverified derived project-tagged mentions. The scoped evidence sample gives the source/item/revision/digest/proposal and quote location. It is an example of the entity's evidence, not necessarily its strongest type claim. The endpoint itself writes nothing.
3. Inspect **Knowledge → Browse** for the candidates' current mentions and owner decisions, opening the original context where necessary. Sample all authority classes present, repeated aliases, conflicting types, and both nested and isolated candidates. Record unresolved cases rather than extrapolating a sampled false-positive rate to all 134 entries. Keep names, quotes, paths and IDs local; share only approved aggregates or synthetic examples.
4. Compare `graphConceptRegions` in the same scope. Require explicit owner-authored, accepted or verifier-supported `contains`/`part_of` evidence for any placement. Trace it in the focused relationship inspector. Do not use `uses`, `references`, repeated mentions or co-occurrence as containment. Check both styles against the same current projection and record scope, counts, denials, interaction and query timings.
5. Review proposed corrections with the owner. Apply only approved individual changes via the existing entity/governance workflow. Preserve originals, proposal history, valid owner decisions and stable IDs. Recheck both projections on the scratch restore before proposing changes to the live catalog.

This slice makes placement depend on the evidence already recorded. It does not reclassify private entities, generate semantic topic proposals, or declare the screenshot counts corrected. The real corpus must still be sampled before changing extraction rules or claiming classification quality. Automated coverage uses synthetic mixed evidence, including a strong model that misclassifies a library and misreads a real quote as containment.
