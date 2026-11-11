# ADR 0037 — Owner-local hybrid retrieval over canonical text segments

**Status:** Implemented as a draft slice for #28. Build, actual host evaluation and cross-platform CI remain unverified.

## Decision

Preserve the existing SQLite FTS5 search as the authoritative deterministic lexical retrieval path (#106). When the owner explicitly configures and enables an **embedding-capable** Ollama model at a loopback address, Praxis can also search derived vector representations of current, completed text segments. Results combine independently ranked lexical and semantic sources through reciprocal rank fusion (rank offset 60), deduplicated by canonical item ID. No synthesized answer, autonomous action or cloud provider is involved.

The existing `LocalTextSegmenter` produces bounded UTF-16 text spans. Schema v16 adds `semantic_vectors`: a **derived-only** table keyed by item, revision, segment ordinal and exact embedding model digest, carrying the source SHA-256, normalized Float32 vector (little-endian), dimensions, bounded segment excerpt and timestamp. A query joins this table to `eligible_semantic_segments`, itself constrained by `eligible_text_search` and a **completed** segmentation job. This independently verifies source active status, latest revision, digest, completed extracted text and completed segment stage at query time. New versions and revocations additionally remove affected vectors through triggers. No source identity, permission or consent grant derives from similar embeddings.

The original extracted text and original file bytes remain canonical; the vector table can be destroyed and recreated. A rebuild computes every in-scope vector **outside** the SQLite transaction and replaces the derived table atomically only after success. If a source is revoked or revised during inference, its rows are not committed because insertion rechecks eligibility inside the transaction. Incremental refresh indexes a bounded next batch, allowing a large corpus to be processed explicitly in stages. A failed model response, cancellation or over-budget full rebuild preserves the previous index.

## Privacy and invocation

The owner must save a separate `local-embedding` model profile. This role is not implicitly enabled by the existing enrichment model, and no model is downloaded or contacted while it is disabled. The saved endpoint must pass the existing `LoopbackEndpoint` validator; `OllamaRuntime` uses a no-proxy HTTP handler, per-request deadline, cancellation and bounded JSON response. Embedding requests use Ollama's `/api/embed` with `truncate=false`, preventing unobserved input truncation. Each request sends at most eight text segments, each at most 2,000 characters; malformed, non-finite, zero vectors and dimensional inconsistency fail without publishing index changes. A model digest change does not silently reuse old vectors.

The first-party Search screen exposes explicit **Update semantic index** and **Rebuild** controls; these use the already approved local model only. Nothing is sent to Claude, Codex or a remote API. The index does not automatically process new source content: the owner controls when expensive embedding work runs. Until indexed, newly captured material is searchable lexically as soon as canonical text extraction completes. Search reports whether it used hybrid or lexical mode; an unavailable local embedding runtime gracefully falls back to lexical hits with an explanation.

## Limits and evidence

- No vector database or speculative generic provider framework. Exact cosine search scans at most 5,000 eligible segment vectors per query; a larger index reports a budget failure instead of silently omitting results. Index refresh defaults to 512 segments, with a hard per-invocation cap of 2,048. A full rebuild exceeding the declared cap fails without deleting the previous index.
- Both query modes return bounded source-linked hits: source ID, item ID, revision, original SHA-256, current locator, title and snippet. Semantic excerpts retain their segment text, not a generated summary; lexical snippets retain FTS highlighting. These are **retrieved evidence**, not statement-level citations or guarantees of relevance.
- The keyword branch is re-read **after** awaited embedding inference, so a revocation or revision during inference cannot resurrect lexical results. Semantic rows are also checked against current canonical eligibility after inference, preventing stale results from a derived cache.
- Reciprocal rank fusion has no hidden confidence threshold. An unrelated semantic neighbor can still appear; no-answer/contradiction/near-match quality must be measured against #29 fixtures before any answer generation. Query results are never treated as instructions.
- The model profile is plaintext owner-local configuration backed up with the existing catalog; vectors are also local private data. No remote multi-account, household sharing or broader consent model is implied (#13/#15/#32/#50).

## Verification

`HybridSearchTests` checks nonlexical conceptual recall, source attribution, duplicate ownership, revocation, revision replacement, a missing derived index, bounded failed rebuild, model digest changes, and revocation during a query-embedding call. `OllamaEmbeddingTests` verifies the actual /api/embed request contract, response order, normalization, malformed responses and input bounds. `ModelProfileTests` validates opt-in/denied desktop paths. Existing catalog upgrade/recovery and lexical golden-corpus tests remain part of the suite.

Run the commands as one chain; a passing `--no-build` test run after a failed build may have executed stale test assemblies.

```bash
dotnet restore Praxis.slnx --locked-mode &&
dotnet build Praxis.slnx -c Release --no-restore &&
dotnet test Praxis.slnx -c Release --no-build --no-restore &&
dotnet format Praxis.slnx --verify-no-changes --no-restore
```

The owner's initial post-merge run after PR #226 observed two build diagnostics (missing `CapturedOriginal` import and xUnit2031) and a formatting failure in desktop fallback. These are addressed in the follow-up to #28, but a fresh, passing post-fix test run and measured embedding performance are not yet recorded. GitHub Actions #137 awaits eligible owner-operated runners. #29 must add measured semantic-vs-lexical retrieval quality, true missing-answer and contradictory-match cases, model/corpus hardware budgets and exact citation scoring. #33 owns grounded answer synthesis and abstention, not this retrieval index.
