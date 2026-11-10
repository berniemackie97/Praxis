# Retrieval benchmark: acceptance and evidence review

**Scope:** [#29](https://github.com/berniemackie97/Praxis/issues/29), evaluation of source-retrieval quality and index integrity. Related feature [#60](https://github.com/berniemackie97/Praxis/issues/60) and answer grounding [#33](https://github.com/berniemackie97/Praxis/issues/33) have **separate** delivery gates. No production similarity cutoff, reranker or answer policy is introduced here.

**State as of November 10, 2026:** The versioned harness, source provenance, reindex/regression tests and local EmbeddingGemma v2/v3 measurements have executed on macOS. Human relevance-label acceptance is outstanding. The GitHub Actions three-OS matrix is wired but has **not executed** because the owner has deferred runner provisioning ([#137](https://github.com/berniemackie97/Praxis/issues/137)). **Do not mark that CI criterion as passed.**

## Observed local verification

Owner-validated merged `main` `cb72c230054e5e3bd7bc72687b09f2800887ad1f` on Apple Silicon / macOS 26.6.2 / .NET SDK 10.0.403 and runtime 10.0.14:

- Locked restore, fresh Release build, full solution tests **607 passed / 0 failed / 0 skipped**, and `dotnet format --verify-no-changes` all completed with command-chain exit 0.
- Optional physical-model comparison: `embeddinggemma:latest`, model digest `85462619ee721b466c5927d109d4cb765861907d5417b9109caebc4e614679f1`, **v2 and v3 both executed and passed** (2/2, exit 0). This validates runner execution and provenance assertions, *not* a minimum relevance score.
- The repository has advanced since this measurement. Treat it as the verified baseline SHA, **not verification of any later commit**. Run new-head deterministic tests after repository changes, and the optional physical-model test again only if retrieval/index/fixture behavior changes.

### Physical EmbeddingGemma results

| Measurement | v2 | v3 |
| --- | ---: | ---: |
| Source originals | 10 | 32 |
| Indexed text segments | 7 | 29 |
| Query judgments | 14 | 36 |
| Positive-source queries | 10 | 31 |
| No-evidence queries | 4 | 5 |
| Index refresh, ms | 191.86 | 626.44 |
| Catalog database + WAL, bytes | 2,099,096 | 4,632,440 |
| Test process working set, bytes | 130,301,952 | 142,360,576 |
| Lexical p50 / p95, ms | 0.68 / 1.47 | 0.79 / 1.70 |
| Semantic p50 / p95, ms | 17.03 / 26.73 | 14.48 / 24.46 |
| Hybrid p50 / p95, ms | 19.10 / 31.82 | 15.42 / 22.13 |
| Semantic/hybrid queries with **no candidates**, among judged no-evidence queries | 0/4 | 0/5 |
| Lexical queries with no candidates, among judged no-evidence queries | 4/4 | 5/5 |

V3 technical positive judgments (16): lexical recall@5 **0.688**, MRR@5 **0.688**; semantic recall@5 **0.969**, MRR@5 **0.938**; hybrid recall@5 **0.969**, MRR@5 **0.906**. V3 video-caption positive judgments (3): lexical MRR@5 **1.000**, semantic **0.556**, hybrid **1.000**. The query named `lexical-distractor` intentionally has a lexical match from a log that is irrelevant to the information need; hybrid currently ranks it before the semantically relevant loader. In the `cross-source-query` case both semantic/hybrid top-five results omit one of the two currently judged sources.

**Interpretation:** All three modes return **candidate sources**, not generated answers. An empty candidate set is the metric's present proxy for no-answer correctness; nearest-neighbor retrieval returning candidates for an unanswerable question does **not itself demonstrate an answer hallucination**. A true answerability/sufficiency gate requires additional architecture and answer-level tests under #33/#60. Do not conflate candidate presence, citation identity and factual answer support. There is no validated release threshold or inferred model-wide performance budget here.

Timing is a **single, warmed macOS run** with serial model calls; the semantic and hybrid timing each include their own query embedding request. p95 derives from 14 or 36 samples, not a load-tested tail latency SLO. The process working sets exclude the separately hosted Ollama model and Metal/unified GPU allocations. `ollama ps` displayed a **685 MB model size** and 100% GPU execution; this is **not** a measured GPU-inclusive resident-memory footprint. A macOS Activity Monitor sample, identified and dated, is required for a defensible host-memory observation. No third-party inference or user data was involved.

## Human judgment review contract

The v2 judgment set (`retrieval-judgments-v2.json`) contains 14 authored cases on the ten-source corpus. V3 (`retrieval-judgments-v3.json`) contains 36 authored cases on 32 sources. **Neither set is independently owner-approved yet.** An owner-uploaded 14-row v2 review TSV was inspected on November 10 and had no nonblank values in the `approved` column.

Review the *relevant source labels* against each original text, not against the ranking output. Mark `yes` or `no` for every case; for `no`, give corrected source key(s), or an empty set for unsupported/unanswerable cases, and a reason. Any changed labels must be committed in the versioned fixture with the rationale, followed by a rerun of **both** deterministic and physical model scoring. A raw authored fixture is not independently reviewed merely because its tests pass.

Use this source-relevance rubric:

1. **Direct support:** the source actually provides pertinent information for the phrased question or search intent. Keyword overlap alone does not establish support; a source can be relevant without an exact lexical match.
2. **Modality/scope:** if a question asks for a video caption, source IDs should be caption evidence; if it is phrased without modality, a source in another supported format may also be relevant. Do not silently assume an unstated format restriction.
3. **Multiple owners / contradictory sources:** independently captured duplicate originals remain separate relevant sources when relevant, and conflicting notes should both be available without treating either claim as verified.
4. **Unsupported and absent:** unsupported CSV/PDF/media text is intentionally *not* indexed in the current fixture. A query requesting evidence available only in an unsupported source has zero **currently searchable** sources. This measures extraction coverage, not whether that information exists in the world.
5. **Lexical expectations are separate:** `lexicalExpected` describes the actual FTS5 term behavior, including deliberately **irrelevant** keyword hits. It is not the list of correct answers; never rewrite it to artificially improve relevance scores.

### Cases requiring explicit judgment rationale

| Fixture / query | Current label | Owner decision needed |
| --- | --- | --- |
| v2 `orbit-concept` (`orbital physics`) | `conflict` | The text notes a *contradiction about orbit distance* but gives no orbital-physics explanation. Is topical mention sufficient for this source-relevance question? |
| v2 `video-concept` (`graphics drawing input`) | `captions` | Is this looking for the VTT lesson, or are other technical documents potentially relevant? The query itself does not request video |
| v2 `cross-source-evidence` (`vertex terrain`) | `captions`, `terrain` | Does this intentionally seek two separate topical sources, or evidence actually connecting vertex processing to terrain? The sources do not substantiate a combined claim |
| v3 `rendering-cue` (`vertex buffer before drawing`) | `render-caption` | The `vertex-buffer` code text also describes streaming vertices before draw calls. Should both be relevant when the question does not request a video? |
| v3 `material-cue` (`material textures`) | `shader-caption` | Other terrain/material documentation discusses textures; decide whether this query is caption-only or general |
| v3 `cross-source-query` (`cliff triangle`) | `terrain-tile`, `mesh-loader` | Each source supplies one concept, but neither combines them. Is the aim independent topical retrieval or a grounded cross-source answer? |
| v3 `lexical-distractor` (`triangle mesh`) | relevant `mesh-loader`; lexical match `keyword-distractor` | Confirm the build log's exact keyword match is not substantive geometry evidence; this intentionally tests precision |

Do not modify fixture labels until the owner resolves these judgments. For the other cases, owner approval of the exported, complete review TSV is sufficient; the list above is **not** a substitute for reviewing all rows.

### Generate and return review materials

From the repository root on current `main`:

```bash
mkdir -p "$HOME/Desktop/Praxis-29-evidence"
for version in v2 v3; do
  if [[ "$version" == v2 ]]; then corpus=retrieval-corpus-v1.json; else corpus=retrieval-corpus-v3.json; fi
  jq -r '.documents[] | "=== \(.id) ===\nPath: \(.path)\nSearchable: \(.searchable)\n\(.content)\n"' \
    "tests/Praxis.Storage.Tests/Fixtures/$corpus" \
    > "$HOME/Desktop/Praxis-29-evidence/$version-corpus-review.txt"
  jq -r '(
    ["case","domain","query","relevant_sources","lexical_expected","approved","corrected_relevant_sources","reason"],
    (.judgments[] | [.name,.domain,.query,(.relevant|join(",")),(.lexicalExpected|join(",")),"","",""])
  ) | @tsv' \
    "tests/Praxis.Storage.Tests/Fixtures/retrieval-judgments-$version.json" \
    > "$HOME/Desktop/Praxis-29-evidence/$version-judgment-review.tsv"
done
```

Edit both TSVs and provide them as evidence. Empty `corrected_relevant_sources` with `approved=no` means no relevant indexed source; include a reason. Keep the author fixture intact until the review and corresponding tested change are merged.

## Required final verification

The GitHub workflow [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) already performs locked restore, Release build and **`dotnet test Praxis.slnx`** on named Linux, macOS and Windows jobs, which includes the deterministic retrieval suites without contacting Ollama. This establishes **configuration coverage**, not actual CI execution. Its jobs currently request three owner-operated self-hosted runners; #137 documents the owner's refusal to provision runners and refusal to pay for GitHub Actions. An explicit **owner waiver of the executed-CI acceptance gate** can allow #29 to close as an evaluation task *with an exception recorded*, but it cannot be described as CI passing. Otherwise, #29 stays open until executing job IDs and a three-OS result are recorded.

Local macOS verification on the **final candidate SHA**, not the earlier baseline:

```bash
git switch main && git pull --ff-only && git rev-parse HEAD
unset PRAXIS_RETRIEVAL_OLLAMA_MODEL
dotnet restore Praxis.slnx --locked-mode &&
dotnet build Praxis.slnx -c Release --no-restore &&
dotnet test Praxis.slnx -c Release --no-build --no-restore &&
dotnet format Praxis.slnx --verify-no-changes --no-restore
echo "Exit code: $?"
```

To rerun the **optional physical local benchmark**, first verify `curl --fail -sS http://127.0.0.1:11434/api/tags` succeeds, then set `PRAXIS_RETRIEVAL_OLLAMA_MODEL=embeddinggemma:latest` and run the two-case `LocalModelRetrievalEvaluationTests` filter in `docs/testing/retrieval-evaluation.md`. Record the model digest, source SHA and full output; do not treat a skipped optional test as a model measurement.

### Closure evidence checklist

- [x] A pinned real-model baseline is measured alongside deterministic lexical/source-revision/rebuild tests; known relevance limitations are reported without an invented pass target.
- [x] The existing CI YAML includes deterministic `Praxis.slnx` tests on the three named platforms.
- [ ] Owner reviews v2 (14) and v3 (36) source labels, resolves disputed judgments, and accepts the documented metric interpretation.
- [ ] Final candidate SHA has local restore/build/tests/format validation (and model rerun if judgments or evaluation code change).
- [ ] Owner measures/records model-host memory or explicitly accepts the listed memory-measurement limitation.
- [ ] Either all three GitHub CI jobs actually run and pass, **or** an explicit owner-approved CI exception is recorded and execution stays tracked under #137 (not marked green).

When these close, #29 may be resolved with the exact evidence and exception status. Parent #60 stays open for separate authorization, evidence sufficiency and answer-quality acceptance.
