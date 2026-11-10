# Retrieval evaluation — lexical, semantic and fused search

**Status (November 10, 2026):** Corpus v1/v2 and source-level provenance evaluation were merged in #222/#230/#232. The owner verified a full 593/593 Release test suite and format exit 0 on macOS using .NET 10.0.403, and ran a real local EmbeddingGemma v2 benchmark (1/1 executed and passed). That baseline exposed 4/4 semantic and hybrid **no-answer false positives**. Corpus/judgments v3 extend ranking evaluation; their latest-head execution and human judgment review are pending. GitHub's three-OS CI remains unexecuted due to owner-deferred runners (#137). None of these tests establishes answer-level grounding or the intelligent MVP (#170).

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

The follow-up to #28 updates those eight assertions to version 16 and scopes the helper's processing-job assertion to the exact claimed item, revision and processor. The patch does not alter catalog migrations, source identity or runtime retrieval behavior. **Historical checkpoint before #232:** a fresh post-fix complete suite was still required here. The owner subsequently verified 593/593 Release tests and format exit code 0 on merged #232; the cross-OS CI matrix has not run.

## Hybrid evaluation v2 — task #29

The versioned `retrieval-judgments-v2.json` uses the same synthetic `retrieval-corpus-v1.json` source identities. It adds authored relevance judgments across code, Markdown, captions, duplicate originals, contradicting claims, instruction-like source text, explicitly unsupported formats, cross-source evidence and no-answer cases. These are **curated test expectations, not independently owner-reviewed relevance labels**. The corpus deliberately retains unsupported CSV/PDF/video originals as negative cases; it does not pretend that those formats are already extracted or that a caption file is a playable video with verified timestamp links.

`HybridRetrievalEvaluationTests` runs real capture, extraction, segmentation, FTS5 and semantic indexing over a disposable owner-private catalog. A declared deterministic topic-vector fixture makes concept retrieval reproducible across platforms. The evaluator records per-domain lexical, semantic-only and reciprocal-rank-fused recall@5, precision@5 and correct empty-result counts, validates canonical source ID, item ID, revision, original digest, path and text evidence, and explicitly detects inappropriate no-answer hits. It tests partial vector refresh, over-budget atomic rebuild, version supersession, revocation, corrupted vector failure, missing-object verification and rebuild recovery. Deterministic vectors are **not** a measure of real Ollama embedding quality.

`LocalModelRetrievalEvaluationTests` measures the **actual** installed, explicitly chosen local Ollama embedding model against these same judgments. It runs only when `PRAXIS_RETRIEVAL_OLLAMA_MODEL` is set; otherwise it prints `NOT EXECUTED`. A normal green suite without that variable **does not count** as a physical-model benchmark. Only authored synthetic fixtures are embedded; the runner never imports an owner's catalog, downloads a model or enables any remote endpoint.

### Repeatable owner-machine verification

```sh
dotnet restore Praxis.slnx --locked-mode &&
dotnet build Praxis.slnx -c Release --no-restore &&
dotnet test Praxis.slnx -c Release --no-build --no-restore &&
dotnet format Praxis.slnx --verify-no-changes --no-restore
```

To isolate the deterministic evaluation and retain full reported metrics:

```sh
dotnet test tests/Praxis.Storage.Tests/Praxis.Storage.Tests.csproj -c Release \
  --no-build --no-restore \
  --filter 'FullyQualifiedName~RetrievalEvaluationTests|FullyQualifiedName~HybridRetrievalEvaluationTests' \
  --logger 'console;verbosity=detailed'
```

For a deliberately authorized **real local Ollama run**, first confirm that the chosen embedding-capable model appears in `ollama list` and that Ollama is available on the same machine. Substitute the actual installed model name; this command does not install or pull anything:

```sh
PRAXIS_RETRIEVAL_OLLAMA_MODEL=<installed-embedding-model> \
dotnet test tests/Praxis.Storage.Tests/Praxis.Storage.Tests.csproj -c Release \
  --no-build --no-restore \
  --filter 'FullyQualifiedName~MeasureInstalledLocalOllamaModelAgainstVersionedSourceJudgments' \
  --logger 'console;verbosity=detailed'
```

The endpoint defaults to `http://127.0.0.1:11434`, and an alternate **loopback-only** endpoint can be supplied through `PRAXIS_RETRIEVAL_OLLAMA_ENDPOINT`. Record the main/PR head SHA, OS and hardware, .NET SDK/runtime, Ollama version, model name and digest, fixture version, full query metrics, index/query wall time and database bytes. The logged test-process working set **does not include Ollama's model memory**: record that separately with host tooling. Do not set speed/memory/quality thresholds until observed on a documented target machine.

### What the measurements cannot establish

- **No-answer:** the current semantic exact scan returns top-ranked neighbors without an abstention threshold. Even unknown/unsupported queries can produce semantic hits. The evaluator reports no-answer false positives; it must not interpret them as truthful answers or silently treat a nonempty hit list as an answered question (#29/#33).
- **Missing originals:** original-object verification fails when physical bytes disappear, but previously extracted text and derived hits can remain queryable. A source digest identifies an original; it is not a live existence proof. Recovery and source-availability state require an explicit later policy rather than silently deleting evidence.
- **Unsupported inputs:** the current text extractor does not supply PDF text, financial row IDs, or media cue-level citation claims in this lexical fixture. Cross-domain proof still requires #181/#184/#170 and independently judged examples.
- **CI:** deterministic tests are part of the full `Praxis.slnx` test suite; #137's owner-deferred runners prevent claims that the Linux/macOS/Windows jobs actually executed. Neither a queued check nor a local macOS pass substitutes for the matrix.
- **Scope:** source/item/revision/digest attribution is source-level retrieval provenance, not statement-level or answer-level citation fidelity (#33). Model relevance, valid no-answer behavior, human judgment review and documented host budgets remain open acceptance work under #29.

## November 10, 2026 — owner-run v2 regression and SQLite repair

After merged #230, the owner ran locked restore, a fresh Release build and the full Release suite on macOS with .NET SDK 10.0.403. **Restore and build passed; 593 tests ran, 592 passed and one failed.** `CorruptDerivedVectorsFailClosedAndMissingOriginalsFailVerification` attempted to inject `x'00'` into a `semantic_vectors.vector` BLOB. Schema v16 correctly enforces `length(vector) = dimensions * 4` and rejected the malformed-length UPDATE with SQLite error 19; the test therefore never exercised the actual invalid-float retrieval guard. The fail-fast chain stopped before format, which **has not yet passed on this head**.

The #29 follow-up modifies only the test corruption fixture: it reads an existing vector, writes `float.NaN` into its first 32-bit IEEE-754 element without changing its length, and updates that one segment. The SQLite length constraint is retained while `LocalSemanticIndex.QueryAsync` must fail closed on a non-finite decoded element. The test then requires a successful derived-index rebuild and verified original-loss behavior. No storage schema or production search change is needed.

**Verification outcome:** the owner subsequently passed the locked restore → Release build → 593/593 full tests → format-exit-0 chain on merged #232. A fresh run is still required for any new unmerged test changes. This is test-fixture repair evidence, **not** a completed real-model benchmark, independently reviewed relevance judgment or executed cross-platform CI (#137).

## Expanded ranking measurement — v3 (#29)

The synthetic v3 corpus (`retrieval-corpus-v3.json`) contains **31 separate original sources, 28 expected-searchable**, including code, Markdown, text, caption sidecars, two independently owned duplicate originals, conflicting notes and untrusted text. Three source originals are intentionally unsupported: CSV financial transactions, PDF and a media file. It has **35 authored judgments** across technical, protocol, storage, operations, video text, research, safety, finance and unsupported-input cases; five are no-evidence queries. The v3 corpus is independent of historical v1/v2 fixtures and deliberately has more than five eligible results, so recall@5 cannot be trivially satisfied by returning nearly every result. Labels are provisional pending independent owner review, and the dataset is still too small to claim general production relevance.

`RetrievalRankingEvaluationTests` runs v3 through the actual capture, text extraction, FTS5 and source provenance assertions without external inference. It verifies exact expected lexical identities and scorers with ranked positive results, incorrect negatives and duplicates. The optional local-Ollama test executes **both v2 and v3** when `PRAXIS_RETRIEVAL_OLLAMA_MODEL` is set, comparing lexical, semantic-only and fused top-five results. Every query reports **ordered stable source keys**, judged relevant keys, and separate per-mode elapsed time. Reports aggregate **recall@1/3/5, precision@1/3/5 and MRR@5 across only positive-answer queries**; no-evidence correctness is a separate fraction. No-answer queries never inflate ranking precision or recall by being assigned a perfect 1. V3 **precision@k uses a fixed denominator k**, even when fewer than k results are returned, and recall@k divides by the number of genuinely relevant sources; no-evidence queries are excluded from both. MRR@5 is reciprocal first relevant rank with zero if no relevant item appears in the top five. The real-model runner records per-mode p50/p95 via nearest-rank order statistics, one host/run only, model digest, index segment count, catalog bytes and test-process memory. Sequential semantic and fused calls each embed the query; these are **not cold-start throughput, single-answer latency, GPU RSS or model cost measurements**. All compared modes query identical authorized synthetic sources.

After checking out the PR and performing fresh Release restore/build, the deterministic verification is:

```bash
dotnet test tests/Praxis.Storage.Tests/Praxis.Storage.Tests.csproj \
  -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~RetrievalRankingEvaluationTests|FullyQualifiedName~HybridRetrievalEvaluationTests' \
  --logger 'console;verbosity=detailed'
```

To execute the real-model comparison, use **only an embedding model already installed in local Ollama**; the example tag below is not a download or a default selected by Praxis:

```bash
PRAXIS_RETRIEVAL_OLLAMA_MODEL='embeddinggemma:latest' \
dotnet test tests/Praxis.Storage.Tests/Praxis.Storage.Tests.csproj \
  -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~MeasureInstalledLocalOllamaModelAgainstVersionedSourceJudgments' \
  --logger 'console;verbosity=detailed'
```

The optional test runs two parameterized cases, v2 and v3. Do not count tests marked `NOT EXECUTED` (the variable is unset) as model measurements. Keep the source SHA, exact installed model digest, OS, .NET and Ollama versions, and full ordered-result output for each run.

### Model-host memory evidence

The owner's initial `ps` sample was taken after `ollama ps` showed no loaded model and therefore only captured idle Ollama processes; it is **not** model memory. To request a ten-minute owner-local load and sample the same resident model:

```bash
curl --fail --silent --show-error \
  http://127.0.0.1:11434/api/embed \
  -H 'Content-Type: application/json' \
  -d '{"model":"embeddinggemma:latest","input":"Praxis memory measurement","keep_alive":"10m"}' \
  > /dev/null
ollama ps
ps -axo pid,rss,%mem,command | grep -E '[o]llama|[l]lama'
```

The `ps` **RSS column is KiB**. Match the actual model runner PID to `ollama ps`; do not sum helper commands or mistake the model's file size for resident memory. On Apple Silicon, unified GPU allocations and shared pages mean RSS is **not a complete GPU/device-memory measure**; record Activity Monitor's process memory or an appropriate host profiler separately if available. A real macOS idle baseline may be reported, but it is not an inference memory benchmark.

### Independent judgment review

Use only the synthetic repository fixture. Export the 35 cases to a worksheet; inspect every `relevant` source against the original content and write corrections rather than silently changing expected IDs:

```bash
mkdir -p "$HOME/Desktop/Praxis-29-evidence"
jq -r '.documents[] | "=== \(.id) ===\n\(.path)\n\(.content)\n"' \
  tests/Praxis.Storage.Tests/Fixtures/retrieval-corpus-v3.json \
  > "$HOME/Desktop/Praxis-29-evidence/corpus-v3-review.txt"
jq -r '(
  ["case","domain","query","expected_sources","approved","correction","reason"],
  (.judgments[] | [.name,.domain,.query,(.relevant|join(",")),"","",""])
) | @tsv' \
  tests/Praxis.Storage.Tests/Fixtures/retrieval-judgments-v3.json \
  > "$HOME/Desktop/Praxis-29-evidence/judgment-v3-review.tsv"
```

Mark each case approved or explain why additional, fewer or different sources are relevant. This is **source-retrieval relevance**; it does not confirm a generated answer, the correct cue time, financial arithmetic or instruction following. The v2 judgment labels also remain provisional unless the owner separately signs them off. Cases with conflicting evidence should preserve both sources; source text containing instructions remains inert. A reviewed corpus and an observed baseline must precede any candidate reranker or abstention threshold selection.

### Completion boundary

This PR extends the evaluation only. Production `LocalSemanticIndex` and `LocalHybridSearch` keep their current behavior; no score threshold, guessed abstention rule, remote provider, changed authorization, database migration or index structure is added. Even with a green suite, the observed no-answer issue remains an **unresolved retrieval-quality limitation** (#29/#60, answer-level #33). Owner-approved ranking and abstention changes require stronger reviewed labels and a repeatable before/after comparison. Unsupported financial row citations, PDF extraction, verified video time ranges and answer-level citations belong to #184/#181/#33/#179. Runner provisioning and three-OS CI are separately blocked under #137; macOS local passes do not satisfy that matrix.

