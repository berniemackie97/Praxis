# Retrieval evaluation — local lexical gold corpus v1

**Status:** A reproducible baseline is implemented in tests; no owner-machine execution or measurements are yet recorded for this branch. This does not establish hybrid retrieval, grounded chat, or intelligent MVP #170 acceptance.

## Scope and expected behavior

The entirely synthetic, versioned corpus is `tests/Praxis.Storage.Tests/Fixtures/retrieval-corpus-v1.json`: ten separate sources and twelve exact source-level query cases. Two sources deliberately contain identical bytes. The expected identifiers are logical source keys, resolved at test execution into actual item/source IDs; no unstable SQLite IDs are committed.

| Domain | Gold cases | Current boundary |
| --- | --- | --- |
| Technical | ParseMesh, terrain context, repeated originals, contradiction evidence, cross-file query, morphological mismatch | Exact token/AND-term search, not semantic synthesis |
| Video | VTT caption wording | Source identity only, not verified time ranges or playback |
| Safety | Missing result, instruction-like source text | Text is indexed as inert evidence; not an agent/tool authorization test |
| Finance | Posted salary and pending bill in CSV | CSV remains unsupported by this text processor |
| Unsupported | PDF and video-file fixture | Originals preserved; text extraction explicitly unsupported |

The executable test captures originals in a new owner-private SQLite catalog, steps the real text processing handler, and compares query results to the gold item IDs. It validates every returned source ID, revision, SHA-256 digest, filename/locator, bounded snippet, and highlight marker; distinct sources with identical originals must stay distinct.

Per-domain recall@5 = expected source identities returned / expected sources. Precision@5 = expected source identities returned / returned sources. For a no-answer case, an empty result is scored as correct and a nonempty result as incorrect. Exact-set assertions catch unrelated matches even when recall is high. This is **synthetic source-retrieval scoring**, not model-answer correctness, statement-level citations, financial arithmetic, or production relevance.

Separate tests check: a new revision hides the previous one even before processing; revocation hides VTT evidence before and after reindex; deleting derived FTS rows is recoverable from current permitted derivatives; over-budget rebuilds do not destroy existing results. Existing search tests additionally cover query bounds, recovery snapshots and older-schema migrations.

## Repeatable commands

Run these as one **fail-fast chain**, so a failed compile cannot be followed by a misleading green `--no-build` test run using previously built assemblies.

```bash
dotnet restore Praxis.slnx --locked-mode &&
dotnet build Praxis.slnx -c Release --no-restore &&
dotnet test tests/Praxis.Storage.Tests/Praxis.Storage.Tests.csproj \
  -c Release --no-build --no-restore \
  --filter FullyQualifiedName~RetrievalEvaluationTests \
  --logger 'console;verbosity=detailed' &&
dotnet test Praxis.slnx -c Release --no-build --no-restore &&
dotnet format Praxis.slnx --verify-no-changes --no-restore
```

The test writes per-domain recall and precision, elapsed query time, synchronous query-thread allocations, SQLite database+WAL bytes, OS and .NET runtime information. These timings are sensitive to host and background load: there are **no invented performance numbers or arbitrary thresholds**. Record the actual commit, hardware/OS, SDK/runtime, command, corpus version, and test output before setting benchmark budgets. GitHub Actions remains blocked on eligible runners (#137), so a queued check is not passing evidence.

## Incomplete acceptance / next work

- The existing UTF-8 extractor supports text, code and captions but not actual PDF text or structured financial records. PDF and CSV cases currently assert that unsupported content is not misrepresented as indexed knowledge.
- VTT text is discoverable, but verified cue timestamps and playable tutorial segments are #181/#177, not outcomes of this harness.
- Hybrid semantic retrieval, multi-file synthesis, evidence-offset citations and truthful answers require #28/#29/#33 and the technical investigation work under #149/#150.
- Owner-scoped retrieval and injection-resistant agent tool approvals require #32/#160; the prompt-like fixture is never executed by this lexical benchmark.
- Finance calculations and provenance must be deterministic and source-scoped under #184/#178, through the shared assistant experience, not a separate Budget workspace.
- #29 stays open for real host measurements, larger cross-domain gold cases (#179), missing-original/partial-index recovery and model-vs-lexical comparisons. A green lexical test is not an intelligent MVP release (#170).

## Hybrid retrieval follow-through — issue #28

The optional owner-local semantic index introduced in [ADR 0037](../decisions/0037-local-hybrid-retrieval.md) is **not** a replacement for lexical corpus v1. `LocalHybridSearchTests` uses a deterministic stub embedding model to assert concept-only recall, fused source attribution, distinct owners of identical bytes, revocation/revision fencing, index-loss recovery and failed rebuild safety. `OllamaEmbeddingTests` checks the actual local embedding transport. Neither suite reports measured embedding quality of a physical local model.

The next evaluation under #29 must record a pinned installed embedding model and digest; compare lexical, semantic-only and fused result identities against independently judged relevant sources; report per-domain recall/precision and no-answer false-positive behavior, query/index throughput, index bytes, local model memory and machine details; and exercise broader cross-file, contradiction, PDF/CSV when implemented, video cue and financial cases. Do not extrapolate quality or performance from synthetic fixture vectors or from a queued CI job.

## macOS post-merge validation — November 10, 2026

After merged PR #228, the owner ran the fail-fast chain on macOS using .NET SDK 10.0.403: locked restore and a **fresh Release build passed**. The fresh suite executed **575 tests: 566 passed, 9 failed**. Eight historical snapshot/migration tests asserted schema version 14 despite the catalog now being at version 16; the hybrid retrieval revocation/revision test helper mistakenly expected a single processing job for an item and processor **across all revisions**, although its two revisions correctly retained separate job history. The fail-fast chain stopped at test failure, so **formatting was not run in this pass**.

The follow-up to #28 updates those eight assertions to version 16 and scopes the helper's processing-job assertion to the exact claimed item, revision and processor. The patch does not alter catalog migrations, source identity or runtime retrieval behavior. **A fresh post-fix complete test and format result is still required; no final-head pass or cross-OS CI result is claimed.**
