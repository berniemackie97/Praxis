# Claim grounding: provisional baseline

This is the first measurement slice for [#538](https://github.com/berniemackie97/Praxis/issues/538), using the shared evaluation ownership in [#311](https://github.com/berniemackie97/Praxis/issues/311).

`tests/Praxis.Infrastructure.Tests/Fixtures/claim-grounding-cases-v1.json` contains 15 **synthetic** source-grouped cases across technical, protocol, finance, operations and media domains. It includes no owner documents. Each group holds a supported paraphrase, a topically related but insufficient passage, and a directly contradictory passage. The fixture is explicitly `reviewStatus: provisional`; the labels and rationales **have not** been independently reviewed by the owner. Do not promote this fixture to a production quality gate by flipping its flag without a source-by-source review.

The `ClaimGroundingBaselineTests` test invokes the **existing** `AnswerVerifier` on each candidate answer and numbered source passage, printing domain-level and aggregate *accepted supported / accepted unsupported / accepted contradicted* counts. It proves the evaluation contract and reports a deterministic baseline; it does **not** claim that `AnswerVerifier` already performs nonnumeric semantic entailment. No model, cloud API, credentials, local private catalog, or token billing is involved. Model tokens and latency are `n/a`, not zero-cost estimates for real model runs.

## Review rubric

- **supported**: the cited passage entails the entire substantive claim, including subject, action, polarity and conditions; a reasonable paraphrase is allowed
- **unsupported**: a passage is relevant or similar but does not prove the claim
- **contradicted**: a passage explicitly asserts the opposite of the claim
- If a label is ambiguous, revise the source/answer or exclude the case before promotion; do not silently substitute the model's guess for independent evidence

Review the answer, cited passage and rationale **together**, without inspecting the baseline checker outcome first. Record corrections in the review PR. Review decisions should be documented before an acceptance score is considered authoritative. Keep at least one source-grouped held-out subset separate from any future prompt or model training examples.

Run the focused diagnostic with:

```bash
dotnet test tests/Praxis.Infrastructure.Tests/Praxis.Infrastructure.Tests.csproj \
  -c Release --no-restore \
  --filter "FullyQualifiedName~ClaimGroundingBaselineTests" \
  --logger "console;verbosity=detailed"
```

The test is not an acceptance gate for semantic support. Only after independent review should a subsequent implementation slice compare no-judge baseline against a proposed narrow deterministic or **explicitly authorized** model-based checker on the same frozen source groups. Report citation precision, false support, unsupported/contradicted handling, abstention, source revision correctness, tokens, latency and permission outcomes; preserve existing source/currentness and disclosure protections. Do not invent a claim-support value from a valid citation number.

## Opt-in local claim-support candidate (#538)

The no-extra-model runtime still accepts all 15 synthetic claims. The optional
`ClaimSupportModelEvaluationTests` test compares that baseline to one structured
claim–excerpt judgment per case, using the existing **loopback-only** Ollama
connector. No judge is wired into chat, no stored source is read, and no external
provider is called. Failures, malformed verdicts, and ambiguous outputs are
never silently counted as evidence of support.

```bash
ollama list
export PRAXIS_GROUNDING_OLLAMA_MODEL='qwen3.5:4b'
dotnet test tests/Praxis.Infrastructure.Tests/Praxis.Infrastructure.Tests.csproj \
  -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~ClaimSupportModelEvaluationTests' \
  --logger 'console;verbosity=detailed'
unset PRAXIS_GROUNDING_OLLAMA_MODEL
```

Optional `PRAXIS_GROUNDING_OLLAMA_ENDPOINT` must be a loopback Ollama URL;
the model profile rejects non-loopback endpoints. When the model environment
variable is absent the test prints `NOT EXECUTED` and does not invoke inference;
a passing test result in that case is **not** a model evaluation.

The candidate emits per-case verdict, reason, wall latency and reported token
usage, plus aggregate false support, precision, abstentions, invalid responses,
model digest and latency. These outputs support comparison; **no accuracy
threshold is predeclared**, because independent owner labels and held-out
source-group evaluations are still outstanding under #311. Even an apparently
good score on five deliberately repetitive groups is not evidence that
production answers are safe. Future integration requires source-currentness
and disclosure gating, claim-to-citation alignment, bounded model retries and
explicitly approved provider/cost policy.
