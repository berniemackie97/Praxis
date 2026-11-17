# Agent research completion — #521 acceptance

This protocol tests the **answering agent** over captured knowledge, not ingestion, ranking quality in isolation, or the later knowledge-maintenance worker. Successful tool calls do not by themselves count as a supported answer. [#523](https://github.com/berniemackie97/Praxis/issues/523) separately owns broader passage ranking and claim-relevance improvements.

## Deterministic checks

`AgentRuntimeTests.ResearchCompletion` exercises full HTTP JSON request budgeting, multibyte owner input, HTTP-provider transport refusal before sending, large paged tool traffic with paired calls/results, live disclosure-policy change, automatic recall usage, exact packet-like hexadecimal literals, opened-source citations, and the distinction between an unsupported answer and a completed answer. The packet fixtures are explicitly **synthetic** and do not establish the actual values or behavior of Conquer 5517 packets.

Run on a clean branch at the PR's exact HEAD:

```sh
dotnet restore Praxis.slnx --locked-mode &&
dotnet build Praxis.slnx -c Release --no-restore &&
dotnet test Praxis.slnx -c Release --no-build --no-restore &&
dotnet format Praxis.slnx --no-restore --verify-no-changes
```

For native macOS verification, quit the app first:

```sh
bash scripts/macos/build-desktop.sh --archive-existing &&
bash scripts/macos/test-worker-session.sh
```

Capture the commit SHA, actual test counts, failures and exit status. A queued GitHub Actions run under #137 is not a passing check.

## Live owner-Mac research acceptance

Use the exact merged/PR build, an installed `qwen3.5:4b` Ollama model, **Compact** context, and **Allow tools** enabled. In a new conversation ask:

> Identify the walking/movement packet used by Conquer 5517. Report the packet identifiers, sizes, and field layout. Use the available source documentation, open the relevant source passages, and cite evidence for each technical value. Do not guess unsupported values.

Use this same owner-supplied prompt for the follow-up comparison against the four failed/partial trials at `835d2e9`. Keep the corpus, model and context setting unchanged. Use the actual indexed walk-packet sources. Do not substitute the synthetic test fixtures, a web article, or a source search hit for an opened passage. Verify that the original source exists and was indexed; otherwise mark the run **not evaluable**, not a model failure or success.

Run this prompt in **three fresh conversations** on the same commit/model/context setting. For each run record:

- completed vs. `context_budget`, `token_budget`, `step_limit`, `no_progress`, `evidence_insufficient` or `disclosure_changed`;
- unique relevant opened sources and whether the displayed citation's source name, revision and locator actually cover each claimed opcode, field, offset, size or processing fact;
- any claimed technical literal absent from its cited opened passage, unsupported numeric/hex warnings, repair attempts or irrelevant/repeated tool calls;
- source search/open call counts and provider-reported input/output tokens **per turn and in total**;
- exact full HTTP JSON body bytes, tool schema bytes, forwarded tool-result bytes and, when present, automatic recall and recap bytes. Byte subcategories overlap and must not be summed with the full body;
- wall-clock elapsed time if available. Record a failed run and its stop reason rather than retrying it invisibly.

The citation UI's source name alone confirms *identity*, not claim support. If the source is private, share **only non-sensitive benchmark figures and a claim-by-claim pass/fail judgment**; don't paste originals, absolute paths, credentials or private source text into issue comments.

Acceptance requires a finite, persisted terminal outcome on each run, no repeat amplification, source-cited technical claims supported by the opened passages, and truthful refusal of unsupported figures. Report all three trials and failures rather than announcing a win based on one sample. Provider tokens are backend-dependent; byte reductions do not prove equivalent token savings.

## Prior observations, not paired proof

| Run | Source task | Input/output tokens | Context |
| --- | --- | --- | --- |
| Initial #521 failure | Walking packet | 351,266 / 1,842 | Repeated searches/opens; no terminal answer |
| Initial #521 retry | Walking packet | 121,545 / 1,726 | No verified answer |
| Owner #531 | Texture upload document | 20,607 / 217 | Three turns; 50,814 cumulative schema bytes |
| Owner #532 | Same texture upload question | 15,296 / 224 | Three turns; 29,236 cumulative schema bytes |

The first two historical walking-packet runs and the texture-upload tests are **different workloads**. They cannot be treated as a controlled before/after token benchmark for the original walking-packet question. The #531→#532 texture runs are one comparable observation each, not a measured failure-rate distribution.

## Closure gate

Close #521 only when full Release and native tests pass, the production HTTP request size guard and verifier tests pass, and owner-Mac walk-packet trials show finite, appropriately grounded answers or truthful, evidence-specific abstention. Record exact tested commit, source judgment, usage measurements and any unsatisfied results. An answer validator cannot prove every natural-language claim semantically; cite/figure checks are deterministic safeguards, not a substitute for inspecting original passages.
