# Living Brain integration

Implementation for [#535](https://github.com/berniemackie97/Praxis/issues/535), extending #445, #440, #488, #489, #511, #512, #514, #329, #331 and #172. This describes delivered code and acceptance boundaries, not a completed production rollout.

## Memory and retrieval

Canonical chat messages, tool calls, sources, immutable originals and owner decisions remain the system of record. Generated recaps, source interpretations, learning suggestions and embeddings remain derived material with provenance. Saving a model statement does not turn it into a verified fact or grant new permissions.

RAG already existed. Automatic Chat recall selects bounded lexical source excerpts after current-revision and provider-disclosure checks. `knowledge_search` and Search can combine keywords with enabled local embeddings. The main chat model navigates the Brain directly; no extra retrieval-model hop is inserted. Local automatic recall and `memory_recall` additionally search up to three relevant private chat recaps through FTS5. Remote and CLI models receive neither this recap index nor its matches. Recap bytes enter the turn's usage receipt.

Recap replay conservatively checks source and playback revocation and current revisions throughout the originating dialogue. Deleting a chat removes its index entries. Older conversations predating per-message citation tracking cannot acquire dependencies retroactively.

Enabled local embeddings receive incremental maintenance in batches of at most 32 segments with a 20-second batch deadline and one-minute polling. New batches wait during active chats or tracked model calls. Changed or disabled profiles discard results; persistence independently validates source revisions. A ten-minute digest probe cache avoids reopening inference on an unchanged empty index. The existing exact semantic scan remains limited to 5,000 vectors. Capacity is now checked before spending query-embedding tokens; capacity, invalid vectors and supported runtime failures fall back to current keyword evidence. This is not an approximate nearest-neighbor index.

## Dream

`/dream` inspects up to three fully disclosable entities from the latest 500 pending reconsolidation revisions. An empty eligible page skips completion inference. `/dream <instruction>` investigates the supplied topic through a bounded evidence/proposal tool surface. There is no subcommand grammar.

Dream caps normal Allow Edits at Propose Edits. The registry independently denies tools outside the operation, including writes, delegation, acquisition, settings changes and installation. Read-only policy now blocks Dream before completion inference because the operation requires review proposals. HTTP runs permit at most eight completion steps, sixteen tool calls, a 48,000 cumulative input-token stop threshold and a 64,000-byte request limit. Thresholds apply between calls; a current provider response can cross the cumulative threshold. Ordinary CLI sessions retain their twelve-call cap; Dream allows up to sixteen relay calls and retains provider-owned context accounting. Dream has a four-minute supervised deadline.

Knowledge proposals retain before/proposed values and entity/evidence fingerprints, deduplicate identical pending payloads, and reject changed snapshots. Snapshot comparison and one correction run together under the catalog writer gate; the correction's existing repository transaction semantics still apply. Source-category proposals also compare the prior category. Failed owner application restores Pending. These fences do not make legacy proposals without snapshots into universally conflict-free transactions.

Activity prepares a new Dream Chat draft with an enabled local, Codex or Claude backend. Model, effort and context remain editable before sending; an unsent draft is preserved. Activity shares the persisted Triage, Reading and Checking role editor with Settings. Remote roles still require disclosure permission.

## Learning from experience

Migration 40 records every admitted first-party run, its selected backend/model/effort, canonical message sequence range, events and final receipt. There is no duplicate transcript store. The worker can retrospectively review clean completed multi-tool tasks containing a deterministic computation/query or a cited opened-source/media observation. Failed, uncited or malformed receipts cannot support a draft. Source observations are explicitly distinct from deterministic results and are not proof that the source is correct or that the task succeeded.

Default policy is **After useful work**, with at most four review attempts and a 24,000-token reservation budget per UTC day. It requires an enabled local Ollama profile and proposal permission. Each attempt reserves at least 6,000 tokens; actual reported input/output usage, model digest and outcome are stored separately. Interrupted attempts count against the budget and have at most two total attempts. Requests use a 4,096-token context, 768-token output cap and 60-second deadline. No eligible evidence means no completion call. Reviews can return no change.

Activity offers Off, Manual review, After useful work and Idle maintenance; Idle waits five minutes after task completion. The local review model and daily budgets are editable. At most four bounded receipts and one relevant current installed skill of at most 2,000 characters enter a packet. A candidate can create a text-only draft or propose a revision against that exact skill version. Sensitive-line checks, existing skill parsing, source-dependency checks and owner review apply. No draft auto-installs. Installed skills still cannot change tools, disclosure rules or executable permissions. Existing owner-approved installation actions remain the only package installation path.

Draft creation and acceptance recheck source dependencies. Later skill discovery/loading also suppresses stale or revoked evidence. Forgetting an originating conversation invalidates dependent skills, including accepted versions. This intentionally favors provenance over retaining an unsupported procedure. These reviews do not train model weights or establish benchmarked general skill improvement.

## Scheduling and recovery

Dream admission, events, attempts and outcomes persist. Worker startup recovers runs before starting processing services. Only proposal-only Dream work can automatically resume, at most three total attempts. It restarts a bounded investigation from canonical history rather than restoring hidden model state. Identical proposals deduplicate. Interrupted ordinary chats fail without replaying arbitrary writes. Owner cancellation remains terminal. A changed/disabled backend or changed selected model/effort prevents queued execution.

An interactive request interrupts an active Dream and queues it for later bounded resumption. Optional **Dream during idle time** is off by default. Once enabled with an automatic learning mode, it schedules local Dream only after a new knowledge watermark, eligible suggestions and the configured interval (120 minutes by default). Idle Dream has its own per-run bounds; the learning-review budget is not a global all-model budget.

Activity processor controls persist pause intent, expose active leases and waiting work, drain active claims, and resume future claims. Intake triage, understanding, enrichment and saved-link study yield new claims during Dream. Capture and deterministic reading remain available. Recaps, project understanding and semantic indexing wait before new passes during Chat; verification waits before dispatch during Dream. Already active calls drain under their own deadlines. This is cooperative scheduling, not instantaneous preemption or a global provider semaphore.

## Saved-video study

Published captions remain the default. Privacy → Links adds explicit media-study consent, maximum duration, sampled-frame consent and an optional installed local vision model. All new media acquisition and visual inference are off by default. Saved-link intent and existing host policy still apply.

For supported YouTube metadata with direct media formats, captionless videos can download bounded audio and run the existing local speech engine. Audio is temporary; its SHA-256 and timed transcript are preserved. Optional visual study downloads a bounded video, samples at most twelve frames, retains JPEG/SHA-256/OCR/timestamp witnesses, and can send batches of at most four frames to an explicitly selected, installed vision-capable local Ollama model. Model descriptions remain labeled unverified and digest-bound. Reported vision tokens survive malformed responses; invalid interpretations are discarded.

Downloads share the public-address/DNS-pinned fetcher, redirect checks, host policy, no cookies and bounded streaming. Limits are 64 MiB audio, 128 MiB video, a configured 1–240 minute duration cap (default 60), a three-minute download deadline per stream and eight-minute study deadline. Settings changes discard new study results; source revision/lease validity is checked before persistence. No paid API, cloud vision fallback, authenticated stream bypass or automatic package installation is introduced. Extraction requires installed `yt-dlp` metadata support, readable direct formats, supported local codecs and available speech/vision capabilities.

Coverage always distinguishes captions, local speech, sampled OCR and unverified visual interpretation. Missing transcripts, decoding failures and unavailable models remain explicit reasons. Sampling is not full-video comprehension. Existing local-video ingestion/playback remains supported; this new visual pipeline applies to saved web-video snapshots, not every local file or web provider.

## Persistence and upgrade

Migration 39 backfills the recap FTS5 table and synchronizes it through insert/update/delete triggers. Migration 40 adds durable work, per-attempt learning usage, skill-experience dependencies, persistent processing controls and opt-in link-study settings. Existing messages and original source stores remain canonical. Back up the coordinated catalog and original store before upgrading a real installation. Schema rollback is not automatic; restore the coordinated backup.

## Verification and acceptance

Run on the owner Mac at the exact reviewed head:

```sh
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --no-restore --verify-no-changes
bash scripts/macos/build-desktop.sh --archive-existing
bash scripts/macos/test-worker-session.sh
```

Regression coverage includes permission isolation, bounded no-op Dream, stale/deduplicated repairs, migration replay, recap restart/deletion/revocation, durable run recovery, changed configuration denial, review budgets, failed-receipt rejection, source dependency invalidation, owner-reviewed skill revisions, processor drain/restart, media consent changes, size limits, invalid visual output, honest coverage and embedding-capacity fallback before inference. Native tests cover Dream draft selection/preservation and decoding of the added control receipts and vision capability.

The restricted environment permits cached-assets Release compilation, direct deterministic test invocation, Swift parsing and native helper typechecking/compilation. Standard xUnit host sockets, fresh NuGet metadata, nested sandbox tests and packaged SwiftUI testing remain blocked. Synthetic native video extraction returns Apple decoder errors here; actual frame extraction and packaged playback are not accepted by this result. The integrated Release build passed with zero warnings/errors; 297 direct focused Fact/InlineData checks and eight architecture checks passed. Three existing focused checks were blocked by sockets or nested sandbox execution. Foundation-only decoding checks for the added native models passed; these do not substitute for the full app test target. Hosted CI remains deferred until December 2026 by owner instruction.

Use a disposable catalog and approved providers for packaged acceptance: local/Codex/Claude Dream; denied remote disclosure; owner correction racing a repair; canceled and interrupted runs; learning Off/Manual/After-work/Idle and exhausted budgets; accepted skill invalidated by source revocation or chat forgetting; pause/drain/restart; captionless speech; sampled visuals on a known clip with frame/timestamp inspection; inaccessible media and changed consent. Record the exact model/digest, source revision, latency, all reported tokens and outcome.

No net token or monetary savings are claimed without held-out A/B tasks. Compare the same corpus, permissions and model against lexical-only and no-new-memory baselines. Include recap generation, indexing, retrieval, retrospective review, retries and failures as well as answer quality and latency. Larger-scale semantic search, automatic skill quality scoring/deprecation, continuous model-weight training and universal multimedia understanding remain outside this implementation.


## Command execution repair — #545

Codex's adapter previously removed `knowledge_propose` from the runtime-approved catalog, leaving Dream able to inspect but unable to queue repairs. The adapter now preserves that owner-reviewed proposal path. The registry still enforces operation restrictions, source disclosure and permission on every call. Read-only or missing-tool preflight stops before completion inference.

The composer now uses a worker-owned command catalog, slash suggestions, a selected-command chip and live command identity. Stored command receipts separate generated narrative from actual review items, source-backed inspections and corrections applied. Canonical message sequences keep receipt placement correct after restart and display-window truncation. A flat indexed derivative of completed Dream receipts avoids repeated JSON scans when skipping unchanged recent reviews. Future idle passes can advance to other candidates; no-op passes still make no completion call.

Migration 41 adds exact owner-statement storage beside the existing memory approval queue and the derived Dream review index. Explicit owner corrections bypass source-answer citation verification through a first-party path. A single bounded mapping pass can use a model without tool support; it can only map exact owner quotes to known, disclosable targets and supported types/relationships. It cannot merge identities. Intervening entity changes and rejected/forgotten source corrections fence pending application. Unknown mappings remain explicit, with the original statement preserved. No automatic region-layout rewrite or externally verified truth is claimed.

Regression coverage includes the reported Codex exposure bug, zero-inference permission denial, command grammar, compact current-turn context, persistent receipts/review cursors, changed evidence, the owner's game/project correction using a tool-incapable model, pending versus direct edits, invalid mapping, exact statement preservation and stale/rejected dependencies. Native grammar/decoding and source parsing supplement the owner-Mac command-picker, Privacy-refresh, proposal-review and correction workflow acceptance. Full packaged SwiftUI execution remains required.


The #545 candidate compiled in Release with zero warnings/errors using cached dependency metadata. Direct focused Fact/InlineData invocation passed 326 cases; three existing socket/nested-sandbox cases remain environment-blocked. Eight architecture checks and 31 roadmap tests passed. Swift app/test parsing, Xcode project/source-membership validation, and executable checks over the actual command/memory DTOs and windowed transcript projection passed. Full SwiftUI typechecking stops at the SDK macro subprocess in this restricted environment; a packaged app build, command keyboard/focus checks, Privacy round trip, and real Codex/local-model workflow acceptance remain required. No measured token or billing savings are claimed.


## Retrospective evidence admission — #514

Completed Chat turns are candidates for learning, not proof that the user's goal succeeded. The local learning reviewer must correlate each tool-result message with an actual successful, non-withheld tool audit record from that run. Computations require nonempty results; data queries require returned rows/groups; source openings require nonblank text; media lookups require a returned moment and citations. A privacy-withheld run is ineligible. Unmatched, failed and empty observations yield `no_verified_evidence` without a model call or skill draft.

This is a conservative gate on **observed evidence**, not independent verification of task success or generalization. Learned skills remain owner-reviewed proposals.

The live calculator smoke exposed an unrelated Chat failure tracked by #549: a self-contained `compute_evaluate` request with literal arithmetic must not pull in incidental Brain excerpts and then demand source citations for a tool-only status answer. Explicit source/file references and owner-scoped calculations still use normal Brain retrieval and citation checks. This does not force models to make two calls or certify failed tool outcomes. The owner-Mac Gemma retests subsequently produced two calculator calls and clean answers; the original failure's raw receipts were not preserved. Failed-attempt retrospective lessons, independent outcome evaluation, safe reuse and rollback remain open under #514. No model weights, new storage or remote permissions are changed.

Manual learning acceptance on the same packaged build produced two `failed` reviews after successful Chat calculator runs. The selected local review model was Gemma 4B; its exact failure is not recoverable from the former generic `failed` state. Review failures now preserve a bounded category: `failed_inference`, `failed_invalid_json`, `failed_invalid_proposal`, `failed_timeout`, or `failed_runtime`. These labels never include raw model responses or private source text. No failure should silently install a skill or be misreported as a successful `no_change`. The owner must test the chosen local reviewer with new eligible work after rebuilding, accounting for previously consumed daily review slots. A distinct UI acknowledgement for empty queue/budget remains open under #550.

At a reviewed branch head, run:

```sh
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test tests/Praxis.Infrastructure.Tests/Praxis.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentRuntimeTests"
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --no-restore --verify-no-changes
bash scripts/macos/build-desktop.sh --archive-existing
bash scripts/macos/test-worker-session.sh
```

In addition to prior learning tests, check forged transcript-only receipts, nonempty audited computation controls, empty computation output and privacy-withheld runs. Model/provider acceptance and measured improvement are separate gates.
