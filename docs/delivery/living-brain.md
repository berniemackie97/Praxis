# Living Brain integration

Candidate implementation for [#535](https://github.com/berniemackie97/Praxis/issues/535), extending #445, #440, #512 and #172. This document describes implementation boundaries, not production acceptance.

## Existing foundations

The canonical catalog already preserves first-party chat messages, tool-call audits, sources, immutable originals, extraction, entity relationships and owner decisions. Quiet-chat maintenance creates bounded local recaps; skills have an existing versioned proposal/review registry. Event-driven deterministic reconsolidation already revisits changed classifications and suggests additional repairs. These are separate from model-weight training.

RAG already exists: automatic Chat recall uses bounded lexical passages with current-source and provider-disclosure checks. The `knowledge_search` tool and Search workspace can combine keyword and local semantic results. Enabling an embedding model does not change the lexical automatic brief into a second model-driven retrieval hop.

## Delivered behavior

- `/dream` selects up to three fully disclosable entities from the latest 50 pending reconsolidation revisions. It reports an empty eligible page without a completion call. `/dream <instruction>` investigates the supplied topic using the same bounded tool surface. It is an intentional runtime operation, not an ordinary prompt alias.
- Dream permits evidence reads and review proposals. Its context caps normal Allow Edits at Propose Edits; the registry independently denies tools outside the operation, including delegated model calls, writes, installation, network acquisition and settings changes. Local-only skill proposals keep their existing review requirement. Normal read-only permission still prevents proposals.
- HTTP-driven runs permit at most eight completion steps and sixteen tool calls, with a 48,000 cumulative input-token stop threshold and 64,000-byte request limit. These are checked between calls; a provider's current response can cross a cumulative threshold. CLI sessions retain their existing twelve-call cap and provider-owned context accounting; they do not gain a hard token guarantee. The supervised Dream deadline is four minutes, with existing stop and persisted transcript behavior.
- Knowledge proposals expose before/proposed values and hash their current entity, alias, mention and relation basis. The applier rejects a changed basis before applying. Identical pending payloads share one review item. Failed review application restores the item to Pending. This is a pre-apply freshness check, not an atomic multi-entity transaction or a universal conflict resolver.
- Local automatic recall and `memory_recall` can search up to three private prior-chat recaps through an FTS5 index. Recaps are labeled generated context, never independently verified observations. Source revisions, active-source state, playback-source revocation and chat deletion fence replay. Remote models receive neither this recap index nor its matches. Relevant recap bytes are included in the existing usage receipt.
- Activity reuses the same role editor as Settings, now including Triage. Provider/model/effort assignments use existing persisted tiers and disclosure policy. Dream preparation selects an enabled Chat backend without overwriting an unsent draft or starting a model call. Model and effort remain editable in Chat.
- Enabled local embeddings are maintained incrementally in batches of at most 32 segments, with a 20-second batch deadline and one-minute polling. No new batch begins while Chat or tracked model activity is active. A running batch drains independently; this is not global queue suspension. A ten-minute digest probe cache avoids repeatedly opening inference on an unchanged, empty index. Changed or disabled profiles discard the batch before storing; the store independently validates current source revisions.
- Semantic runtime failures, invalid vectors and the existing 5,000-vector exact-scan capacity limit fall back to current keyword evidence. This does not replace the exact scan with an approximate index. It preserves operation when a corpus exceeds current semantic capacity.
- Saved-link receipts distinguish queued acquisition from learned content. Video text labels published captions versus metadata-only capture and states that visual content has not been analyzed. Existing supported link/caption intake remains the only acquisition path here.

## Persistence and recovery

Catalog migration 39 creates the recap FTS5 table, backfills existing generated recaps, and installs insert/update/delete synchronization triggers. Conversation rows and original messages remain canonical; no second transcript store is created. Normal catalog backup includes the derived index. A schema rollback is not automatic; use the existing coordinated backup and restore process before upgrading a real catalog.

Chat-derived summaries retain dependencies conservatively: any stale cited source in reviewed dialogue suppresses that conversation's recap, even if a newer recap no longer discusses it. This favors avoiding stale disclosure over recall completeness. Undisclosed private skills and unconfirmed model suggestions cannot grant tool authority.

## Verification

Run at the exact reviewed head:

```sh
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --no-restore --verify-no-changes
bash scripts/macos/build-desktop.sh --archive-existing
bash scripts/macos/test-worker-session.sh
```

Regression fixtures cover operation restrictions despite Allow Edits, direct-command isolation, empty-queue no-op, finite maintenance steps, private recap restart/deletion/revocation, denied entity metadata, duplicate/stale proposals, disabled/changed embedding profiles, idle indexing, revised sources, capacity fallback and honest media coverage. Native tests check Dream preparation, model choice and unsent-draft preservation.

Owner-Mac acceptance must use a disposable catalog and approved providers. Verify local/Codex/Claude Dream, decline remote evidence permission, review an exact proposed repair, revise its source before approval, stop a run, restart, delete a recalled conversation, and index a new source. Record exact model, source revision, outcome, latency and total input/output tokens. Hosted CI remains deferred until December 2026 under the owner's instruction.

The restricted implementation environment cannot refresh NuGet metadata, start the standard test host's socket, run nested Swift compiler macros, or provide full packaged-app acceptance. Cached dependency assets permit Release compilation. Direct invocation of xUnit Fact methods provides additional deterministic evidence but does not replace the standard host, theories, fixtures, native tests or live-provider acceptance. Record final counts in the PR; do not turn this limitation into a passing-suite claim.

## Remaining product work

No net token or monetary savings are claimed. Compare held-out tasks against the prior path with the same corpus, source rights and models. Include recap creation, indexing, retrieval, every review hop, failures, retries, output quality and latency in total cost. Keep lexical-only and no-new-memory baselines; do not assume that more stored knowledge or an extra model pass helps.

Autonomous experience-to-lesson promotion and evaluated skill improvement remain #488/#489/#514; governed new-tool execution remains #160. Captionless-link transcription and visual comprehension remain #329/#331. Stage pause/drain/resume and durable maintenance checkpoints remain #511/#445. Cross-provider conversation disclosure and broader semantic reconciliation remain #440/#442. No continuous weight updates, arbitrary self-installation, full-video learning claim or unattended semantic Dream scheduler is introduced.
