# ADR 0043: Model-tiered intake, category disclosure policy and the agent runtime

**Status:** Accepted (owner decisions, November 13, 2026). Schema v21. Implements #184, #362; advances #31, #32, #33, #173, #291–#307. Extends [ADR 0034](0034-subscription-verification-ladder.md) and [ADR 0041](0041-hybrid-local-first-deployment.md); does not relax [ADR 0003](0003-processing-trust-boundaries.md) security spaces.

## Context

Praxis is LLM-powered: the owner talks to a model, and the model reads, relates and acts on the second brain through tools. Intake must be model-driven — documents arrive in any layout and must be understood, categorized, related and stored without per-format templates — while answers involving money, dates and counts stay exact. The owner's Mac (M4, 16 GB) runs a ~9B local model at roughly 14 tokens/second, so the local model cannot carry heavy intake without becoming the bottleneck. The owner has Claude and ChatGPT plans (Claude Code, Codex CLIs) and API keys for several providers.

An earlier draft of #184 replaced the model with hand-written heuristics (column-name tables, keyword categories, regex statement layouts) and a built-in monthly budget summary. That is rejected: it is brittle, duplicates what models do well, and puts product semantics (what counts as "spending") in code instead of in the agent's reasoning.

## Decision

### 1. Division of labour: models understand, code reads, checks and computes

| Work | Done by | Why |
| --- | --- | --- |
| Bytes → text/structure (PDF text, OFX, delimited rows, later OCR/ASR) | Deterministic readers | Exact, instant, no disclosure |
| Document type, data category, sensitivity, routing | Local model (low trust) + format hints | Must happen before any disclosure decision |
| Layout understanding, transcription, merchants, categories, relations, summaries | Strong model (structured output) | Quality; arbitrary layouts |
| Applying a model's mapping to thousands of rows | Code | A short model plan, executed exactly — no per-row tokens |
| Verification (balances reconcile, quotes/amounts printed in the source, vocabulary, direction) | Code | Never hallucinates; the ladder's arbiter |
| Disagreement, failed checks, sampled audits | Strongest model | Spend only when evidence says so |
| Budgets, analyses, plans | The agent at chat time, through generic query tools | No built-in budgeting feature |

Model output is a proposal until code verifies it. A model never grants permissions, never chooses its own disclosure, and never writes canonical originals.

### 2. Model tiers

Three owner-configured tiers: `local`, `strong`, `strongest`. Each names one provider profile; any provider can fill any tier:
- Local Ollama (loopback), other OpenAI-compatible local servers.
- Subscription CLIs: Claude Code, Codex (ADR 0034 invocation rules: no shell, empty private directory, API-key variables stripped, bounded output, deadline).
- Metered APIs with owner keys: Anthropic, OpenAI-compatible (OpenAI, DeepSeek, OpenRouter, Gemini's compatible endpoint). Keys live only in the macOS Keychain.

A tier left empty is skipped. Every invocation is recorded (tier, provider, model identity, purpose, data category, tokens where reported, outcome).

### 3. Data categories

Every source receives exactly one category before any remote processing: `financial`, `correspondence`, `health`, `identity`, `personal`, `documents`, `media`, `code`, or `unclassified`. Format hints decide first (an OFX download is `financial`); otherwise the local tier classifies from a bounded excerpt. Without a usable local model or confident hint the category is `unclassified`, which is treated as the most sensitive. The owner can correct a category at any time; a correction re-evaluates pending work.

### 4. Disclosure policy

A **mode** sets the default for remote disclosure:

| Mode | Remote processing of a category/provider pair |
| --- | --- |
| `local_only` | Never |
| `ask_each_time` | Each item waits for an approval decision |
| `standing_grants` (default) | Allowed where the owner granted that category to that provider; otherwise the fallback chain |
| `allow_all` | Allowed for every category except those the owner explicitly denies |

**Grants and denials** are owner-confirmed rules over (category, provider, purpose ∈ {intake, chat}), optionally expiring. Explicit denials beat grants and modes. Sources in an ADR 0003 local-only security space are never disclosed regardless of mode.

**Fallback chain** (per category, ordered) when remote processing is not allowed or the provider is unavailable — no item waits indefinitely:
1. `local_if_verified` — run the local tier; keep the result only if it passes the same code verification used for strong-tier results (balances reconcile, amounts printed, schema valid). Otherwise continue.
2. `hold` — create an approval item with a deadline (default 72 h). Choices: approve once, grant the category to this provider, redact and send, keep local, skip. On expiry, continue.
3. `index_only` — keep the source captured and text-searchable; no derived facts.
4. `skip` — record the decision; nothing derived.

Default chain: `local_if_verified → hold → index_only`.

**Changes from chat.** The agent may *propose* policy changes. Changes that narrow disclosure or permissions apply immediately. Changes that widen them become a pending change the owner must confirm in the first-party UI; a model, a document or a tool result can never confirm one. Every disclosure and policy change is appended to an audit log.

### 5. Agent tool permissions

Independent of disclosure: `read_only`, `propose_edits` (writes become reviewable proposals), `allow_edits` (writes to Praxis-owned derived knowledge). System actions (#297) are out of scope here and remain default-deny. Widening follows the same confirmation rule.

### 6. Intake ladder (by signal)

`read (code) → triage (local) → heavy (strong) → verify (code) → [strongest on failure/disagreement/sample] → store with tier provenance`. Each step consults the disclosure policy for the source's category and the step's provider. For financial documents:
- OFX/QFX/QBO is a published format and is read by code.
- Delimited exports: code reads rows; the strong tier receives the header and a bounded sample and returns a column/date/sign **mapping**, which code applies to every row and verifies (running balance when present; every row parses).
- PDF statements: code extracts text; the strong tier transcribes rows; code verifies opening + Σ rows = closing and that every amount is printed. Failure escalates to strongest, then to the fallback chain.
- Merchants and categories come from the strong tier per document in one batched call, stored as model annotations with provenance.

### 7. Agent runtime

One provider-neutral runtime with two backends:
- **Tool loop:** Praxis runs the tool-calling loop against Ollama, OpenAI-compatible or Anthropic chat APIs.
- **CLI agent:** Claude Code or Codex runs headless with only Praxis tools allowed (shell, file edit, web and other tools disabled), reaching them through an MCP relay process that connects to the running worker over an owner-private Unix domain socket with a per-session token. The catalog stays single-process.

Tools are generic and read-first: search sources, read source excerpts with locators, list financial documents and accounts, query transactions with filters, grouping and sums (exact integer minor units), read and propose policy. Tool results are disclosures: the runtime filters results by the disclosure policy for the session's provider before they reach the model. Conversations, turns, tool calls and citations are persisted.

## Relationship to verification disclosure (ADR 0042, #366)

ADR 0042 classifies each source `local_only` or `remote_verification` and governs **only** external verification of reviewed evidence; it states that its classification authorizes nothing else. This ADR governs **intake and chat**. The two are enforced at different call sites and never widen each other: a source an owner pins local-only here is never sent for intake or chat, and ADR 0042's per-request verification consent is unchanged. Converging both into one owner-facing source policy (one place to see and change what may leave the Mac, per operation) is tracked as follow-up under #32.

## Consequences

- Heavy intake depends on a configured strong tier or explicit fallbacks; with neither, sources remain captured and searchable but not deeply understood. This is visible, not silent.
- Subscription CLIs are rate-limited; per-provider daily allowances (ADR 0034) apply to intake as well as verification.
- Exactness comes from code verification and query tools, not model arithmetic.

## Delivery

- **PR A:** tiers, categories, disclosure policy engine and audit, schema v19, and the financial intake rework onto the ladder.
- **PR B:** agent runtime, generic tools, both backends, persistence and desktop protocol.
- **PR C:** macOS chat, approval and policy interfaces.
