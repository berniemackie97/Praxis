# Stabilization and acceptance

Owner direction, November 23, 2026: stop capability expansion and make existing intake, retrieval, graph exploration, Dream and learning dependable. A merged implementation is not an acceptance result.

## Reproduced intake failure

The watched-folder scanner reused the selected-folder limit of 10,000 files. A larger approved Inbox failed before reconciliation, while the desktop showed only `Intake scan rejected input.` Retrying could not change that outcome.

Watched scans now retain a lexically ordered page of at most 10,000 files, advance on each pass while retaining stability observations across pages, and wrap to the start after the final page. Metadata traversal remains a complete, cancellable walk on every pass; this is bounded-memory pagination, not an incremental filesystem index. Discovery keeps only the page and a depth-bounded traversal, rather than materializing every directory entry. The 2 GiB hashing budget still applies per reconciliation. Verified stamps and quiet-period observations survive page changes, each with a 100,000-entry cap. A continuously changing early file cannot hold the sweep on its page. A changed file before the current cursor is reconsidered after the sweep wraps; restart begins a fresh sweep and original digest/request identity prevents duplicate revisions.

Selected-folder inspection and manual imports retain their strict file-count and byte limits. Both routes use the same ignore, project, link and depth checks. Folder depth, access failures and races can still reject a scan; these are not universally isolated per-subtree failures. The owner-local status now includes the concrete invalid-input reason.

Errors are retired against the full discovery walk, independently of stability observations. This fixes an oversized deleted file leaving a permanent degraded status after its observation had already been discarded. Preserved originals are unaffected.

Repeated filesystem notification faults previously rebuilt the watcher and invalidated file observations on every iteration. Watcher recreation now waits 30 seconds while periodic polling can complete the quiet period and capture stable files. A polling fallback is still reported as degraded; it is not evidence that OS notifications work.

## Work that belongs in code

Code files use the same extension classification as the document reader and bypass per-segment semantic extraction before model connection. They retain deterministic reading, indexing and project-level understanding. Generated technical evidence already had this bypass. This removes redundant requests on that path; it does not prove better semantic recall or repair old failed jobs automatically. Ordinary prose keeps its existing extraction behavior.

`knowledge_graph` reads the existing desktop graph projection. It exposes bounded region metadata, pending understanding counts, hubs and paged members with confirmed relations. It does not create another graph or infer facts from layout. Dream may use it to choose evidence to inspect. Remote sessions require an explicitly authorized source, and entity metadata remains subject to the existing full-evidence disclosure check. Region summaries are omitted because project-purpose summaries can have a broader source scope. Remote responses also omit entity-derived group and theme labels; authorized folder labels and disclosure-checked hubs remain available.

Dream remains bounded to three entities, eight completion steps and sixteen tool calls per run. Unfocused candidate discovery still reads the latest 500 pending reconsolidation revisions. Graph access alone does not deliver complete, resumable reconciliation across the whole Brain. That limitation remains open under #445/#535/#545.

## Reference implementation standards

These are patterns to evaluate against the existing .NET implementation, not dependencies added or code copied in this change. Check licenses and pin an upstream revision before importing implementation code.

| Reference | Behavior to evaluate | Praxis acceptance evidence |
| --- | --- | --- |
| [Hermes memory](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/features/memory.md) | Compact standing memory with on-demand SQLite session recall | Current-source citations, correction/revocation tests and actual prompt bytes for repeated questions |
| [Graphify](https://github.com/Graphify-Labs/graphify) | Queryable structure and explained edges; deterministic code extraction | One canonical graph, evidence drilldown, useful retrieval against raw-file search at equal answer quality |
| [LangGraph](https://github.com/langchain-ai/langgraph) | Durable execution and inspectable state | Crash/restart/retry matrix without duplicate publication or replay of unauthorized actions |
| [Deep Agents](https://github.com/langchain-ai/deepagents) | Context management, on-demand skills and tool-level boundaries | Schema/output/context budgets, no-progress termination and matched task success across local/Codex/Claude |
| [LangChain](https://github.com/langchain-ai/langchain) | Model/tool contracts and reusable integrations | Connector conformance, cancellation, malformed output, provider denial and measured cost |
| [Open SWE](https://github.com/langchain-ai/open-swe) | Asynchronous coding-agent workflow | Resumable task receipts, scoped execution and verifiable review outcomes before broader autonomy |
| [Odysseus capability policy](https://github.com/odysseus-dev/odysseus/blob/dev/website/original-harness-capability-policy.md) | Capability ownership and bounded, traceable media ingress | One permission authority, bounded inputs and explicit unsupported/partial states |

## Delivery order and measurable gates

Use existing work items rather than creating parallel features:

1. **Intake recovery and format correctness:** #552/#557/#558/#559. Exercise the actual failing Inbox locally, then synthetic 10k+ files, file changes between pages, temporary and oversized files, renamed/revoked sources, links, denied access, malformed files, cancellation, model outage and restart. Map every advertised format to deterministic reader coverage and explicit unsupported/partial outcomes.
2. **Efficiency and answer quality:** #507/#556/#311/#315/#379. Establish exact model/digest and corpus baselines. Compare model calls, actual input/output tokens, cache/reuse, wall time, memory, source recall, citation precision, correct abstention and completed tasks. Cheaper or shorter prompts alone are insufficient. Include local small models, Codex and Claude under the same task/disclosure scope.
3. **Graph and Dream:** #445/#535/#545 and the existing graph acceptance work. Replace ambiguous empty-region explanations with observable capture/understanding/verification state. Progress through a durable candidate inventory, investigate contradictions and duplicates, preserve owner corrections, and report unresolved work with before/after evidence. No-op prose is not completed maintenance.
4. **Learning and model improvement:** #487/#491/#493/#530. Keep reviewed procedural skills, retrieval memory, routing calibration and changes to model weights distinct. Review examples and rights, split by source/version to prevent leakage, establish a held-out baseline, evaluate a candidate and keep a rollback. Existing curation scripts and skill learning are not proof that a model has been trained or improved.

No private Inbox content is included in committed fixtures or upstream research. Real-provider performance, full format coverage, comprehensive reconciliation and fine-tuning remain acceptance work; none is certified by compilation.
