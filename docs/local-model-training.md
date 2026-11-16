# Train a local Praxis adapter

Praxis owns the assistant identity. The selected model is an engine, visible in Chat. Model naming and ordinary identity replies do not require weight training. For facts that change, retrieval stays the source of truth: notes remain attributable, correctable, and removable.

The shared behavior and specialist roles are specified in [the model-family contract](model-family-behavior.md). All owner-trained generative tracks start from candidate reduced-refusal foundations, with their capabilities and regressions evaluated rather than assumed.

The useful first experiment is a small **LoRA adapter** that improves Praxis behavior: when it should search, cite a real source, ask for missing evidence, withhold private data, or request approval. This is the scope of [#313](https://github.com/berniemackie97/Praxis/issues/313). It is not pretraining from scratch, and the captured Brain is not a training set.

1. Start with owner-approved examples of successful answers and corrections. Make them synthetic or de-identified by default. Include misses, contradictory evidence, revoked sources, instructions hidden in notes, wrong vendor identity, fabricated timestamps, and requests to install a tool. Store only the minimum messages needed to reproduce each decision.
2. Keep a manifest with authorization, example IDs, revisions, and digests. Stage the export outside watched folders. Never include raw account records, credentials, transcripts of uninvolved people, or unscreened intake. Removing a note does not remove facts learned by an adapter; discard and retrain the affected adapter if an included example is revoked.
3. Write `train.jsonl`, `valid.jsonl`, and `test.jsonl` as conversations. Split by source and task family before training, so paraphrases or versions of the same example cannot land in both train and test.
4. Run the same held-out suite against the unmodified model plus Praxis prompt/retrieval, and against a few-shot variant. Compare factual grounding, citations, abstention, identity, policy cases, tool validity, and latency. Treat any permission regression as a failed experiment.
5. Run an on-device pilot with [Apple’s MLX-LM LoRA workflow](https://github.com/ml-explore/mlx-lm/blob/main/mlx_lm/LORA.md). The first owner-Mac training candidate is [Qwen2.5 1.5B Instruct abliterated 6-bit (MLX)](https://huggingface.co/mlx-community/Qwen2.5-1.5B-Instruct-abliterated-6bit); its inference and in-memory LoRA initialization were checked, but gradient training and behavioral quality are not yet verified. Pin the exact model revision and runtime version before comparing runs. On a 16 GB Mac, measure short sequences and a batch of one first, then increase only if needed. Use source checkpoint weights that the trainer supports, rather than trying to train the installed Ollama GGUF in place.
6. Keep the candidate separate from the default model. Where the resulting weights are compatible, [Ollama can import Safetensors or GGUF](https://docs.ollama.com/import); verify format, model template, and tool calling before enabling it in Praxis. Keep the baseline available for rollback.

Example of an eligible synthetic training row:

```json
{"messages":[{"role":"user","content":"Does the attached procedure say to unplug the device first?"},{"role":"assistant","content":"The excerpt does not say. I need the safety section or a source that covers that step."}]}
```

## Praxis Chat G1 candidate staging (#483)

The first importer is [scripts/training/curate_chat.py](../scripts/training/curate_chat.py). It admits only the Hugging Face-reviewed **safe** Tulu 3 Personas Parquet file at revision `76c414d` with SHA-256 `19a16c5f1649d367f69899b3cfadbbeb5ffef91f24e20c6617588bdd87cd3e60` (ODC-BY). It never clones repositories, runs dataset-supplied scripts or downloads other dataset files. [Source and file scan](https://huggingface.co/datasets/allenai/tulu-3-sft-personas-instruction-following/tree/main/data).

Use a workspace outside the repository and watched intake folders. Parquet conversion requires `pyarrow` in the chosen Python environment:

```bash
python3 -m unittest discover -s scripts/training -p 'test_*.py'
python3 scripts/training/curate_chat.py fetch --workspace "$PRAXIS_TRAINING_DATA"
python3 scripts/training/curate_chat.py curate --workspace "$PRAXIS_TRAINING_DATA" --limit 2000
```

`raw/` contains the verified source; `candidates/` contains split conversation JSONL, a separate row-level provenance map and a digest manifest explicitly marked **unreviewed**. Repeating the same output name fails rather than overwriting. Selection is deterministic, but this first slice does not check near-duplicates, personally identifying content, model-specific token lengths, upstream authorizations beyond the declared dataset license, or factual correctness. Its small internal test split is **not** the independent golden evaluation gate (#311). No files are training-approved until quality, rights and leakage review; tool-convention conversion (Hermes), broader datasets and preference training are separate slices.

No training or export of private data has been run. The next input is a reviewed example set and a held-out acceptance set, not a copy of the Inbox.
