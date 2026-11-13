# ADR 0044: One generic intake engine with pluggable readers and record profiles

**Status:** Accepted (lead-architect decision with owner, November 13, 2026). Schema v23. Supersedes the financial-only intake path of [ADR 0043](0043-tiered-intake-disclosure-and-agent-runtime.md) §6; keeps its tiers, categories, disclosure policy, holds, fallbacks and agent runtime unchanged.

## Context

ADR 0043 built the model ladder (code → local → strong → verify → strongest) but wired it only into a financial handler. Every other document stopped at local enrichment, and formats without a text decoder (PDF, Word, spreadsheets, email, HTML, images) were marked unsupported. The owner's intent is one intake that understands *anything* dropped into a watched folder, with cheap models doing light work, strong models doing the heavy reading, and the strongest model checking and correcting.

The owner asked whether domain-specific (financial) code is itself an anti-pattern. It is not. The anti-pattern is a domain-specific *pipeline*. Deterministic domain code is the cheapest, fastest and most exact tier there is: parsing OFX costs zero tokens and cannot hallucinate a balance. We therefore want **more** domain code, placed as plugins on one generic pipeline, never as a parallel intake path.

## Decision

### 1. One stage graph for every source

```
capture → read (code) ─┬→ headings, segments → [local enrichment, owner toggle]
                       └→ triage (local, every source) → understand (strong → verify → strongest) → commit
```

All stages run on the existing durable processing queue (leases, retries, backoff, supersession, revocation). No category, file type or domain selects a different path. Categories select only the disclosure policy and which record schemas are requested.

### 2. Readers: bytes → normalized document (code, zero tokens)

`IDocumentReader` plugins, chosen by content signature first and extension second, produce a `ReadDocument`: normalized text with typed locators (page/line, sheet/row, header, cue) plus an optional structured payload. Readers in this decision: plain text and code, Markdown, subtitles, PDF (text layer), DOCX, XLSX, CSV/TSV, OFX/QFX/QBO, QIF, EML, HTML, and images and scanned PDF pages through on-device OCR (Apple Vision, via a bundled helper the worker invokes without a shell, with bounded input, output and time). All readers enforce size, entry-count, decompression-ratio and depth limits. A format with no reader is a reader bug, never a terminal "unsupported" state; reopening runs automatically when a reader is added.

Coverage by source type:

| Source | Read by | Notes |
| --- | --- | --- |
| Notes, Markdown, text, config, logs | plain-text reader | encoding-tolerant; extensionless text sniffed |
| Code files | plain-text reader (code format) | each file is a source; see *Code projects* below |
| Word, Excel, CSV/TSV | OOXML and table readers | rows kept as structure for code-side mapping |
| PDF | text layer, OCR for scanned pages | page breaks preserved for citations |
| Email (EML) | MIME reader | headers, best body part, attachment inventory |
| Web pages (saved HTML) | HTML reader | scripts and styles dropped |
| Images, screenshots, scans | on-device OCR | |
| Video | subtitles and captions (existing caption path) | audio transcription: follow-up, same contract |
| Bank and card exports | interchange readers (OFX/QFX/QBO/QIF) or table reader | typed transactions with zero model calls |
| Links (`.webloc`, `.url`, URLs in notes) | follow-up | fetching is network egress: governed by the disclosure policy as a `web` purpose with owner approval, SSRF-safe (no private addresses), size- and time-bounded |
| Code projects (a folder or repository) | follow-up | a collection-level understanding stage over member sources (structure, purpose, dependencies), reusing per-file results |

The read stage keeps its processor identity (`source-text-v1`), so search, segments, citations and the agent's `knowledge_open` immediately cover every readable format. Audio transcription is out of scope here: on-device speech needs an owner permission flow in the app and lands behind the same reader contract (videos keep their caption path).

### 3. Triage: local tier, every source, one bounded call

Format hints decide first (an OFX file is financial with no model call). Otherwise the local tier reads a bounded excerpt and returns category, document type, language, a one-line gist and the record kinds it expects. It is low-trust: it chooses policy and schemas, never final facts. Without a usable local model the source is triaged from hints or left `unclassified` (strictest policy) and intake continues. Owner-set categories are never overwritten.

### 4. Understand: strong tier does the heavy reading, code verifies, strongest corrects

One structured call per document (or per chunk for long documents) on the strong tier returns a summary, topics, entities, relations and typed records, each item carrying a verbatim quote. The prompt includes only the record schemas triage predicted, keeping prompts short.

**Verification is code and per item.** Every quote must be found in the document (normalized whitespace); every number and date a record states must be printed inside its quote; entity and relation names must occur in their quote; records must satisfy their profile's schema and normalization. Items that fail are dropped individually. The whole result escalates to the strongest tier (with the failure reasons and the strong output, so it corrects rather than restarts) when a profile proof fails (for example, statement balances do not reconcile) or more than a quarter of items fail. If the strongest result also fails, the verified subset is kept and the document is marked for review. Disclosure is decided per step and provider exactly as in ADR 0043; when remote use is not allowed, the category's fallback chain applies (verified local result, time-limited hold, index-only, skip).

**Why not have the strong model orchestrate local workers for intake?** On this hardware the local tier runs at ~14 tokens/s. Fanning a document out to local workers is slower and no cheaper than one strong call over a compacted document, and it multiplies low-trust output that must then be re-verified. Local stays at what it is fast and private at: triage, routing, and the fallback when a category may not leave the Mac. Chunking, merging and de-duplication are done by code. Delegation to cheaper tiers remains an agent capability at chat time (`agent_delegate`).

### 5. Record profiles: domain knowledge as plugins

An `IRecordProfile` contributes a record kind's schema fragment, normalization, verifiers and optional projection into a typed dataset:

| Profile | Records | Verifiers | Projection |
| --- | --- | --- | --- |
| Transaction | dated amounts with counterparty, direction, merchant, category | amount and date printed; document-stated totals and balances reconcile (exact integer minor units) | financial ledger (accounts, statements, transactions) |
| Event | appointments, meetings, trips with start/end | dates printed in quote | records dataset |
| Deadline | obligations and due dates with amount where stated | date (and amount) printed | records dataset |
| Contact | people and organizations with roles and contact points | values printed | knowledge entities + records |

Structured payloads bypass model transcription: OFX/QIF transactions come from code and the strong tier only names merchants and categories in one batched call. Delimited files are mapped once per header layout; the mapping is cached by header fingerprint, so a bank's next export is read by code alone.

### 6. Token and cost discipline

- Identical originals (same SHA-256, for example moved or duplicated files) reuse prior triage and understanding: zero tokens.
- Documents are compacted before prompting (whitespace, repeated headers and footers, table rendering) and bounded by a per-document token budget; chunks beyond the budget are processed only on the strong tier and merged by code.
- Per-provider daily allowances (ADR 0034) and per-tier concurrency (local 1, remote 2) bound load; background polling backs off while the worker is busy.
- Every call is recorded with tier, provider, category, purpose, tokens and outcome.

### 7. Persistence (schema v23)

- `intake_documents`: per item revision — format, pages, document type, language, summary, category origin, status (`understood`, `partially_verified`, `held`, `index_only`, `skipped`, `failed`), reason, tier and provider that produced the result, token totals.
- `intake_records`: typed records with kind, normalized fields (JSON), quote, offset, tier and verification state.
- `intake_layouts`: cached delimited mappings by header fingerprint.
- Topics, entities and relations are committed through the existing proposal store (producer `intake-understand-v1`), so the knowledge graph, review and the agent's tools see them unchanged. Strong- and strongest-tier items that pass verification are recorded as verified; local items remain low-trust suggestions.
- The financial ledger becomes the transaction profile's projection; its rows are produced by the understand stage. The separate financial records and categories processors are removed; existing financial derivations are recomputed by requeueing understanding for current revisions.

### 8. Owner experience

Every source shows its intake: how it was read, triage result, which tier understood it and how much was verified, records found, and any hold with a link to Approvals. The Library lists document type and summary. Nothing is silent: index-only and skipped states say why.

## Consequences

- Any readable document gets the full cascade; adding a format is one reader, adding a domain is one profile.
- Exactness comes from code verification and typed projections, never from model arithmetic.
- Heavy intake depends on a configured strong tier or explicit fallbacks; without them sources are captured, readable and searchable but not deeply understood, and the intake panel says so.
- OCR runs on device; transcription of audio is a follow-up behind the same contract.
