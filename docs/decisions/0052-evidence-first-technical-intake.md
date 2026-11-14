# ADR 0052: Evidence-first routing for technical intake

**Status:** Draft (PR #413). Builds on ADR 0044 (generic intake), ADR 0046 (confirmed knowledge) and ADR 0051 (model activity).

## Problem

Per-file model reading of generated reverse-engineering outputs consumed substantial subscription usage without reliably creating the call relationships explicitly recorded in the source. A hidden local-provider daily cap also prevented the configured Ollama model from working despite being reachable.

## Decision

- Preserve every original, searchable text derivative, version and source locator. Route known generated artifacts away from generic triage, local semantic extraction and remote document understanding; do not treat an absent model as a capture failure.
- Run a separate leased `technical-evidence-v1` processor after text extraction, using the same provenance, revision, revocation and evidence-span checks as other knowledge producers. The first parser recognizes documented class headings and inheritance in Markdown, plus explicit direct call operands in supported disassembly JSON exports. It excludes registers, indirect calls and comments rather than inventing a target. Binary and data-resource names in Markdown are typed from their literal suffixes.
- Treat a Git marker as repository grouping evidence without importing `.git` internals or granting permission to disclose the repository. A Git-only project is mapped locally; multi-file remote reading requires a separately captured build/project marker and remains subject to the broader project disclosure audit.
- Make Ollama's local calls uncapped. Keep explicit daily allowance controls for remote providers. Historical activity failures remain visible in the call history without presenting them as a current outage.
- Require evidence for each decisive verification verdict and fail closed to `uncertain` when the cited text is not within the corresponding item's quoted evidence.
- Bound the generic document prompt and avoid a paid model call solely to combine chunk summaries. Long documents are explicitly marked incomplete rather than represented as fully understood.

## Boundaries

The artifact detector also treats JSON, JSONL, NDJSON and XML under a directory named `evidence` as generated material. **That is a routing convention, not proof that the parser understands their schemas.** Files without recognized call/declaration syntax remain preserved and searchable, but may yield no structured knowledge. The activity/understanding status must not be interpreted as proof that every symbol, edge or record has been extracted. Expanding supported technical formats should use explicit readers and schema-specific local extraction, with source-grounded tests.

Local call edges prove only what the visible instruction says. They do not infer virtual dispatch targets, control-flow reachability, runtime effects or behavior from unresolved addresses. A source filename used as the calling-symbol label must be checked against the owner's evidence conventions. These claims are narrower than a complete reverse-engineering knowledge graph.

Project-level prompt construction and per-member disclosure authorization require a separate security audit; an anchor source's approval cannot be treated as authorization for every member of a multi-file prompt. Do not claim that project-level remote disclosure is certified until that invariant is tested.

## Acceptance evidence

The earlier development session reported 749 queued text jobs replayed in 1.2 seconds, ten graph edges for `GameDataSetQuery` after having none, and 321 artifacts routed locally which had previously accounted for 289 remote calls and 8.25 million reported input tokens. These are **reported results from that replay**, not a reproducible end-to-end quality benchmark, a measure of all ingestion stages or a promise of identical savings.

Before merging: run the locked restore, formatting check, Release build, complete .NET tests, macOS Swift tests and isolated catalog migration/replay on the PR head; review denials and revocations, non-technical files inside technical folders, Git-only project behavior, graph precision/recall and current Activity status. Reconcile the test evidence and PR text with the resulting commit. CI queued without a runner does not count as passing.
