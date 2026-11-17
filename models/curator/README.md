# Praxis Curator 4B — training experiment

Working name only. This is **model research and training**, not a Praxis application/database migration. The model learns evidence-grounded knowledge maintenance independent of a particular storage schema.

## Foundation

- Upstream checkpoint: [Qwen/Qwen3.5-4B](https://huggingface.co/Qwen/Qwen3.5-4B) (post-trained, Apache-2.0). Training code requires an immutable 40-character upstream commit SHA.
- Training: CUDA BF16, Unsloth + Transformers 5 + TRL completion-only supervised learning. Do not use GGUF as a training input or 4-bit QLoRA. The `lora` mode is a mechanics check; `full` updates language-model weights and attempts to freeze vision components, refusing to train if it cannot identify them.
- Target: evidence-preserving, temporal, identity and relationship reconciliation with explicit abstention and no-op decisions. No personal Brain corpus, private sources or data from any earlier experiment are used.

## Dataset and evaluation

`cases/train.jsonl` and `cases/eval.jsonl` contain independently authored **synthetic seed fixtures**, not an adequate production training corpus. Evaluation cases are never used for gradient updates. The `group` key identifies a source/scenario family; no group is allowed to overlap splits. A production dataset needs owner-reviewed labels, rights metadata, provenance, adversarial coverage and separate held-out corpora.

The response contract is `decision`, `target_id`, `related_id`, `evidence_ids`, `reason`. Evaluation checks valid record IDs and eligible citations, decision accuracy, exact evidence agreement, and false knowledge-changing operations. A structurally valid response is **not** necessarily semantically correct.

```bash
python3 -m unittest discover -s models/curator -p 'test_*.py'
python3 models/curator/evaluate.py --ollama qwen3.5:4b \
  --output /path/outside/repo/curator-baseline.json
```

The baseline runner sends **only the explicitly supplied case packets** to `127.0.0.1:11434`, never to a remote service. The included fixtures are synthetic. Do not supply private cases to remote endpoints without separate authorization. The output file must not already exist.

To independently score a recorded JSONL prediction file (`{"id":"...","response":{...}}` per line):

```bash
python3 models/curator/evaluate.py --predictions /path/predictions.jsonl
```

## Training

Run on a BF16-capable NVIDIA CUDA GPU with enough VRAM and a separately installed, version-captured Unsloth/Transformers 5/TRL environment. Follow [Unsloth's Qwen3.5 training instructions](https://unsloth.ai/docs/models/qwen3.5/fine-tune). Use a fresh, untracked output path **outside** the repository and any watched intake directories.

```bash
python3 models/curator/train.py --revision <40-char-upstream-commit> \
  --mode lora --steps 10 --output /path/outside/repo/curator-lora-smoke
```

This command downloads the pinned upstream model and runs actual gradient steps; it has **not** been executed as part of the repository-only setup. Re-run on a separate output path with `--mode full` only after validating the model parameter map and available memory. A ten-step run establishes mechanics, not learned competence. All training output stays unpromoted until held-out testing demonstrates real improvements without safety/provenance regressions.

Training outputs record weight artifacts and a revision/version/dataset digest manifest. Model and dataset releases, environment package lockfiles and controlled promotion still need dedicated acceptance before a production run.

## First measured gate

1. Baseline Qwen3.5-4B with the synthetic independent eval set and capture exact outputs, invalid citations, unsafe edits, latency and model version.
2. Expand separate train/eval sets from independently verified knowledge-maintenance decisions, grouping cases by source family before any paraphrasing.
3. Validate a real BF16 training step and held-out scoring. Do **not** claim success from a decrease in training loss alone.
4. Progress to extensive full-parameter training only with enough reviewed examples, measured feasibility and an unchanged held-out evaluation gate.
