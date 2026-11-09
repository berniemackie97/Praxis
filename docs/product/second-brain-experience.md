# Praxis product experience

**Status:** Product intent and acceptance direction. This document is not a list of shipped features.

Praxis is a local-first **personal knowledge intelligence and agent platform** that works **without Obsidian, a cloud account, or an AI provider**. The product is not complete when it merely browses or searches files: it must progressively interpret supported knowledge, connect corroborating evidence across sources, investigate questions, and produce actionable, cited conclusions without inventing facts. It accepts owner-approved sources, keeps originals and source identity, makes supported content searchable, and lets the owner build meaningful connections across projects. Obsidian remains an optional editor/source adapter.

## Actual product MVP: intelligent Second Brain

**Owner decision (November 9, 2026):** The first product release must be an integrated, production-quality, agent-powered system. A functional Inbox, local text index or collection browser alone is a foundational delivery checkpoint (#130), not an MVP release. The cross-stage acceptance gate is **#170**.

A representative set of owner workflows (all required at the supported MVP scope):

1. Open Praxis on a Mac; designate a local root and import a mixed personal knowledge corpus: technical projects and notes, an instructional video, and explicitly provided bills/income/transaction records. Automatic capture/reconciliation preserves originals and exact lineage.
2. Deterministic parsers and approved local agents extract, classify, normalize and **correlate** supported entities, structures, claims and relationships. No document or model hallucination becomes canonical fact; unrecognized formats remain explicit. Reprocessing corrects derived outputs and preserves reviewed owner decisions.
3. Build quality-evaluated lexical/semantic/structural retrieval, selective evidence expansion, reranking where measured useful, and tool-assisted bounded investigations. Ask a complex domain question; get a specific supported explanation, traceable source/version/span references and transparent disagreement/abstention, not a file list.
4. Use a **persistent first-party Praxis assistant** to ask questions or request tasks. Inspect scope, plan, tool arguments, model/location, budget, progress, verification and audit; approve/revoke side effects. The model never grants itself permission.
5. Operate with **local-first inference** (measured Ollama/Qwen where capable), cache/provenance/context budgets and explicitly approved escalation to supported stronger models. Evaluate official and terms-compliant routes for Claude, Codex, Gemini, Grok, DeepSeek and other providers. Never equate app subscriptions with unrestricted API access, or silently transmit source material to remote inference.
6. Browse a **linked, provenance-aware derived Markdown representation in Obsidian**, and optionally export inspected, versioned, task-specific context packs for coding agents. Root/nested `AGENTS.md` and provider-specific context instructions are **reviewed projections**, not authority granted by imported source text; avoid floods of repetitive context.
7. Pass audited correctness, retrieval, prompt-injection, resource, backup/recovery, permission, privacy and real macOS workflows with concrete benchmark fixtures and reproducible run evidence. Production-grade is a testable engineering bar; no model can guarantee perfect understanding of arbitrary input.

### Cross-domain MVP proof — not technical-only

**Technical research** is one vertical slice, not the architecture's defining specialization. Two additional concrete, mandatory MVP experiences:

- **Video / tutorial retrieval (#176/#177):** Praxis captures a supported local instructional video, builds time-aligned transcript and source-aware segments (and visual evidence only when implemented), and answers "Is there a tutorial on X?" with a relevant **inline playable segment in first-party chat**. Show accurate segment boundaries, the source's identity, and a jump to the full original video at the precise timestamp. A random frame, transcript-only link or invented timestamp does not pass.
- **Financial planning (#176/#178):** From explicitly imported income, bills and transaction exports, Praxis reconciles recurring and variable costs, deterministically calculates month-scoped totals, highlights potential savings with assumptions, and generates an **editable, verifiable monthly budget** linked to the underlying records. Handle duplicates, account transfers, refunds, missing data, permissions and private-content disclosure. A live banking API is *not* required; it remains separately permissioned and subject to provider availability.
- **Shared evaluation (#179):** Test technical citations, video segment retrieval/playback and finance arithmetic as separate gold-standard workflows against the **same core evidence, permissions, versioning, routing, RAG and agent architecture**, with domain-specific extractors and output widgets only where warranted.

The product must ultimately support many more types and sources—audio, images, email, calendars, social media, structured events—but the MVP proves **general-purpose extensibility by actually delivering these different workloads**, not by promising universal comprehension. Sensitive local financial data is never sent to remote inference without separately scoped explicit consent. Financial calculation is deterministic; probabilistic models may propose interpretations or budget scenarios, not fabricate totals.

**Delivery ownership:** #170 cross-domain integrated acceptance; video and financial feature #176/#177/#178 and evaluation #179; technical extraction #149/#152–#154; evidence/investigation #150/#155/#156; retrieval/evaluation #29/#172; models #31/#61; review/relationships #115/#116; grounded assistant #33; scoped task execution #32/#34/#173; safe tool proposals #160; context projection #143; Obsidian views #57/#171. Local foundation #130 remains its own prerequisite.

**First release boundary:** one owner, one local machine, representative supported technical, video and financial corpora, a measured set of officially available inference/tool integrations and one functional approved task workflow. Broader bank/social connectors, universal binary comprehension and remote multi-device sync are separately gated; they do not weaken the intelligence/quality requirements above.

## Financial intelligence and live account updates

The first supported budget demo may begin with a verified local export, but **the intended product is not a CSV-based budgeting application**. That export is one possible source adapter. Praxis will eventually connect to explicitly authorized financial data providers and receive incremental updates about accounts, transactions, income, bills and other financial activity, where provider access and user consent permit.

Each update should arrive as a typed, versioned, source-attributed event through the common connector envelope (#151/#157), with provider/connection/account identity, stable external IDs, amounts and currency, transaction state, merchant, timestamps, original response evidence, receipt/digest, synchronization status and permission scope. Store immutable observations and a reconciled current projection: a pending card authorization later posting for a different amount must not be charged twice, and refunds, reversals, corrections, transfers, repeated webhooks, missing updates and revocation must have explicit behavior.

Link financial entities and events to accounts, payees, recurring obligations, income sources, budget categories, owner notes, goals and relevant facts from other sources. Preserve the distinction between provider-provided metadata, deterministic rules, owner-reviewed classifications and model hypotheses; propagate corrections without silently rewriting original events. Use incremental processing to avoid needless model inference on already structured data. Privacy is independent of assistant prompts: only scoped approved connections and permitted data can be analyzed, synced or disclosed to a model, and no financial movement is authorized by ingestion.

Provider feasibility, consent scope, rates, costs, refresh/webhooks and data protection are tracked in #162. The typed event contract is #157. The owner-facing financial reasoning and budget workflow is #178, and the complete agent/MVP acceptance remains #170. **A local CSV demo is neither a prerequisite for future financial feeds nor a substitute for their reconciliation design.** Real bank integration is a later connected capability, not an implemented feature.

## Core experience

The owner can drag files, choose a folder, type or paste a thought, dictate a note, or explicitly save a web page. Approved integrations—including a browser extension, Obsidian and a future MCP server for agents—feed the same ingestion contract. An input folder is a convenient *inbox*, not the authoritative database and not a mandatory physical organization model.

Every intake item has visible, inspectable status: **received → safely stored → extraction pending/running → searchable** or **unsupported / failed / permission denied / canceled**. Processing may be retried or reindexed without losing the accepted original. Users should not need to interpret ZIP files or hidden directories to know what Praxis contains.

The first-party Praxis interface provides an Inbox, Library, Search, source detail, simple Projects/Collections and processing diagnostics. Search results link back to the exact source and explain whether the evidence came from original text, extracted text, a transcript or a derived suggestion. UI details and responsive layouts are design tasks, not assumptions about technology stacks.

## Original versus derived knowledge

| Layer | Owner-controlled meaning | Recovery and mutation policy |
| --- | --- | --- |
| Source | Where information came from: dropped file, chosen directory, URL, recorded voice, browser selection, connected app, editor or agent | Distinct source ID, origin, capture time, consent/scope and optional refresh policy |
| Original | Exact accepted content bytes or text, including unsupported formats | Immutable or versioned original, digest and bounded size; never silently rewritten by extraction or AI |
| Canonical item | Stable identity and versions associated with one or more sources | Cross-source duplicates can share stored bytes without merging provenance, permissions or deletion intent |
| Extraction | Text, metadata, image dimensions, transcript or parser output | Versioned by source digest and extractor; errors visible; regenerable |
| Index | Terms, snippets, eventually embeddings | Derived cache only, rebuilt and purged after source update/revocation |
| Suggested knowledge | Tags, topics, entities, summaries and inferred relationships | Model/rule provenance, status and confidence context; accepted user edits retained across reprocessing |
| User organization | Explicit projects, tags, saved searches, notes and relationships | Independent of the source directory and never overwritten by inferred categorization |

**No silent file moves.** Folders and suggested categories may be shown as views, not imposed by moving originals into a generated hierarchy. A file may relate to many projects, topics and people.

## Input types and truthful initial support

| Input | Initial accepted behavior | Deeper processing after validation |
| --- | --- | --- |
| Markdown / plain text | Preserve original, UTF-8 text extraction, searchable terms | Internal links, heading references, source-level entities |
| PDF / common documents | Preserve original; selected bounded PDF text extractor after evaluation | OCR, layout, tables and document-level citations |
| Images | Preserve bytes and safe file metadata; mark unprocessed where unsupported | OCR, visual captions and object/entity proposals with model consent |
| Recorded audio | Explicit record/stop, preserve original and processing status | Local or specifically approved transcription with editable versioned text |
| Video | Preserve original and supported container metadata | Transcripts, bounded keyframe analysis, chapters and scene indexing, separately justified |
| Source code / repositories | Preserve files and extensions; safe text extraction of supported code | Repository-aware symbols, references and relationships—never execute imported code |
| Game assets / archives / binaries | Preserve original, integrity digest and safe metadata; state unsupported analysis honestly | Opt-in format-specific parsers and limited extraction, never treat arbitrary binaries as understandable |
| URLs, pages and bookmarks | Explicitly capture source URL and permitted metadata; offline bookmark-only fallback | Safely fetched page snapshot, article text, link graph and refresh history |
| Other/unknown | Preserve or reject explicitly under size/path/security policy with visible reason | No invented content, summary or semantic claims |

Imported source content is **untrusted data**, including code comments, webpages and prompts embedded in documents. No source may grant agent privileges or instruct Praxis to access unrelated private resources.

## Capture surfaces

### Local Inbox (first delivery)

- Drop files or explicitly select directories; later opt into a specific watched intake folder.
- Preserve input location and bytes without modifying the source.
- Idempotent imports, separate source identity versus byte deduplication, and restart-safe job progress.
- Show unsupported processing as metadata-only rather than silently discarding files.

Work items: #99–#105, #114. The first usable local experience is #107–#108 and retrieval #106.

### Voice dictation

- Owner deliberately starts and stops a recording; microphone use is visible.
- Save raw audio first. Transcription can be pending, edited or declined independently.
- Record the transcription processor, model and language when applicable; local versus remote processing requires distinct consent.
- No always-listening microphone or silent remote audio upload.

Work items: #109–#110.

### Browser extension

- User invokes Save URL, Save Selection or Save Page. Request permissions narrowly.
- Browser/page content cannot call the local Praxis API as an authenticated user.
- Explicit local pairing, visible queue/retry status, safe URL and HTML handling, and no automatic browsing-history capture.
- URL-only capture does not imply a successful article extraction or license to archive restricted content.

Work items: #104, #111–#112.

### MCP and agents

- Agents use a **separate scoped interface**, not access to a vault path or the user's database.
- Start with paired, read-only search and fetch, returning stable IDs, bounds and source provenance.
- Capture or modify only via separately granted operations, independent authorization and audit/approval policy.
- Prompt injection in retrieved records cannot change permissions; revocation must immediately limit future access.
- Local-only comes first. Remote agent access, OAuth and host identity need explicit security design and device validation.

Work items: #117–#119, with #32 protecting write actions.

### Obsidian and other connectors

- Obsidian is an optional editor/capture adapter (#5/#57), not the start screen or source of truth.
- Later GitHub, calendar, email and document connectors (#30/#35) require specific scopes, retention and removal/refresh semantics.
- Cross-device sync (#4) is a distinct capability, not a prerequisite for capturing and searching content locally.

## Search, organization and trustworthy answers

First ship local, rebuildable lexical search with titles, paths, excerpts and source citations (#106). Hybrid/semantic retrieval follows with relevance evaluation and unauthorized-result tests (#28/#29/#60). Derived tags, entities and relationship candidates are reviewable (#97/#115/#116), not hidden source rewrites. Grounded research/chat (#33/#61) must cite exact inputs and abstain when evidence is missing. Authorized workflows (#32/#34/#62) never inherit permissions from model output or external source text.

## Deep knowledge intelligence: defining product outcome

The owner may import entire development projects, assembly/disassembly exports, decompiled routines, technical notes, format documentation and experiments. Praxis should extract supported code and format structures, identify versioned symbols and dependencies, cross-reference research, and distinguish observed facts from unresolved hypotheses or conflicting descriptions.

A representative acceptance question is **"How does the Conquer 5517 client process Dmap.dat?"** or **"How do I parse Conquer C3 files?"** Praxis must correlate the relevant source and disassembly evidence, identify what the corpus establishes, supply an explanation or implementable algorithm with exact citations, and explicitly abstain where evidence does not suffice. Neither vector retrieval nor plausible model prose is proof of correctness.

Pipeline: (1) immutable accepted originals and typed provenance; (2) bounded format-aware deterministic extraction; (3) versioned technical facts, symbols and evidence relationships; (4) optional model-generated candidates with review and contradiction handling; (5) bounded, authorized investigation tools; (6) grounded synthesis and evaluated answers. Reprocessing preserves owner corrections and invalidates stale derivatives. Unsupported binary formats are not automatically understood.

Work items: deep technical feature #149 with contract #152, extraction #153 and claim/reconciliation #154; bounded investigation feature #150 with runs #155 and evaluation #156. Existing #29/#31/#33/#97/#115/#116 retain retrieval, model choice, answering and human organization ownership.

## Typed, permissioned, incremental connectors

A connector is an independently authorized source adapter, not an ambient privilege. Known provider semantics must enter Praxis as typed, versioned events: provider/account, source/event ID, event type, schema version, occurred/observed times, consent/scope, revision/receipt and content reference. A bank transaction already labeled `transaction.posted` need not consume an LLM request to rediscover its type. Events may later produce cross-domain relationships only with verifiable evidence.

Recover missed events using bounded reconciliation, replay-safe IDs, revision history, rate-limit backoff, deletion/revocation semantics and inspectable progress. Start with scoped GitHub changes #158 and authorized local development/agent sessions #159; investigate bank and social provider availability/cost through #162 before implementation. Email/calendar remain tracked in #35. Do not scrape banking credentials, infer service permissions or bulk-monitor a user account without explicit opt-in.

Work items: #151 typed history feature, #157 event contract, #158/#159 first source experiences and #162 sensitive-provider research; #30/#32/#34/#35 own provider priorities, grants, durable work and outward-facing consent.

## Agentic capabilities and voice

Praxis has its own chat/assistant user experience (#33), not merely file browsing or MCP exposure to third-party agents. Agentic investigations require a bounded plan, evidence retrieval, hypothesis checks, current authorization at every step, token/tool/time budgets, source-cited results and visible uncertainty. A model may propose new tools and request approval (#160); it may not create executable authority or install/run its own unreviewed code.

Prefer deterministic processing and a measured local inference backend (for example an already-installed Qwen model via Ollama) for routine tasks; optional stronger remote reasoning is explicitly user-approved per source and operation, with disclosure of destination and usage. Claude/Codex subscriptions are not presumed to grant unrestricted API access. No remote model is an automatic fallback. Preserve a no-model ingest and local search path.

Beyond note dictation (#109/#110), later first-party push-to-talk interaction (#161) can use free offline STT/TTS with clear microphone control and text fallback; spoken instructions cannot silently approve sensitive actions. The product must remain useful without audio hardware or any paid inference/voice service.

## Foundational local demonstration (#130; not the complete MVP)

1. Start Praxis on a supported desktop without installing or opening Obsidian and without network access.
2. Import a mixed, bounded test folder containing Markdown, one supported PDF, an image, a video, a code file and an unknown binary. Every accepted file is present with original digest and a transparent extraction state.
3. Restart mid-import; retry to a consistent catalog without duplicates or damaged originals. A second source holding identical bytes retains separate attribution.
4. Search a phrase from supported text and navigate from result to the exact source; unsupported media is discoverable by metadata only.
5. Rebuild the derived index and reproduce search results; delete/revoke a source and verify it disappears from results as policy requires.
6. Inspect and export/restore original bytes and metadata; distinguish full backups from a single-file restore.
7. Prove no private content is transmitted to an external host and that unrelated processes or browser pages cannot read the local service.

This demonstrates the local foundation, **not** the release-ready intelligent Second Brain. The complete product MVP is the integrated gate #170; agent-assisted knowledge, Obsidian views, model routing and task execution are not optional future aspirations. None may silently expand remote trust or promise universal file comprehension.

## Not yet decided

The exact catalog/database, parser runtimes, local service packaging, microphone/transcription engine, UI framework, web-extension browser support, MCP SDK/transport and optional model provider must be selected through bounded spikes and evidence. Avoid premature microservices, vector databases, complex graphs or broad plugin abstractions before a measured use case requires them. Remote trust/hosting, key custody, account sharing and recovery choices remain in #50.

The implementation order and slice acceptance are in [standalone MVP plan](../delivery/standalone-mvp.md). Milestone/issue hierarchy is defined in [roadmap](../roadmap.md) and the [declarative GitHub plan](../../scripts/github/roadmap.json).
