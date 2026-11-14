# ADR 0053: Source-scoped project summary retrieval

**Status:** Proposed — [#421](https://github.com/berniemackie97/Praxis/issues/421), stacked on [#415](https://github.com/berniemackie97/Praxis/pull/415)

## Decision

Keep the canonical `intake_projects` rows and existing project detection. Persist the exact source IDs, item IDs, revisions, paths, digests and category/local-only state that contributed to each generated summary in the existing JSON details. No second source index or new model-specific database.

Before returning model-derived project details, validate that those source revisions remain active and unchanged, the captured category state still matches, the generating tier still points to the same provider, and any required intake disclosure still allows the provider. A failed check returns a map-only result with model-produced fields absent. Previously stored rows without verifiable source scope are not trusted.

The `knowledge_project` tool additionally checks every contributing source against the **current recipient's chat disclosure policy**. Remote agents receive no cached project details or even project names from a summary when any contributing source is withheld. Local owner use remains available, but invalid model-derived details are not presented as current. Chat disclosure is re-evaluated for each tool call so permission revocation takes effect within an active turn.

A project also persists a fingerprint of the effective intake disclosure scope across its captured members and configured model tiers. File changes retain the existing debounce, but narrowing or widening source permission changes can trigger regeneration without a changed file fingerprint. Stable denied scopes do not repeatedly request model work. Regeneration may use only the sources now authorized for the selected provider. Revalidation does not grant remote access.

## Tradeoffs and limitations

This favors withholding an entire mixed-source summary over guessing which model-produced sentences depend on a restricted source. It preserves safe local file search and avoids inventing unsupported claim-level provenance. Existing summaries without scope need regeneration before a remote agent can use them. Repeated live checks add database reads; optimization requires a measured policy-version invalidation mechanism rather than a time-based grant cache.

No structural code parsing, external source execution, hidden source reuse or cross-agent disclosure authorization is introduced by this decision. Chat transcripts already sent to a backend cannot be retroactively erased when a grant is revoked.

## Verification

Synthetic tests cover stored-source revocation, changed categories, same-fingerprint regeneration, legacy scope absence, expired or removed grants, recipient-specific withholding, and mid-turn chat grant withdrawal. Release build, full .NET suite and macOS app/worker acceptance are required before merging.
