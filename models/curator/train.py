"""Qwen3.5-4B supervised training entrypoint for CUDA GPUs, not macOS MLX."""

import argparse
import hashlib
import importlib.metadata
import json
from pathlib import Path
import re

from contract import SYSTEM, load_cases, user_prompt


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--train", type=Path, default=Path(__file__).parent / "cases/train.jsonl")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--revision", required=True, help="40-character upstream Hugging Face commit SHA")
    parser.add_argument("--mode", choices=("lora", "full"), default="lora")
    parser.add_argument("--steps", type=int, default=10)
    parser.add_argument("--sequence-length", type=int, default=2048)
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-f]{40}", args.revision):
        parser.error("--revision must be an immutable 40-character commit SHA")
    if args.steps < 1 or args.sequence_length < 256:
        parser.error("steps must be positive and sequence length >= 256")
    if args.output.exists():
        parser.error("output path already exists; never overwrite a candidate or run")
    cases = load_cases(args.train)

    # Environment prerequisites are intentionally imported only on an actual training run.
    import torch
    if not torch.cuda.is_available() or not torch.cuda.is_bf16_supported():
        raise RuntimeError("Qwen3.5 training requires a BF16-capable CUDA GPU")
    from datasets import Dataset
    from huggingface_hub import snapshot_download
    from unsloth import FastLanguageModel
    from trl import SFTConfig, SFTTrainer

    base = snapshot_download(repo_id="Qwen/Qwen3.5-4B", revision=args.revision)
    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=base, max_seq_length=args.sequence_length,
        load_in_4bit=False, load_in_16bit=True, full_finetuning=args.mode == "full",
    )
    if args.mode == "lora":
        model = FastLanguageModel.get_peft_model(
            model, r=16,
            target_modules=["q_proj", "k_proj", "v_proj", "o_proj", "gate_proj", "up_proj", "down_proj"],
            lora_alpha=16, lora_dropout=0, bias="none", random_state=3407,
            use_gradient_checkpointing="unsloth", max_seq_length=args.sequence_length,
        )
    else:
        # The model is multimodal. Full fine-tuning updates language weights; vision stays frozen.
        vision_names = [name for name, _ in model.named_parameters()
                        if any(part in ("visual", "vision_tower", "vision_model") for part in name.split("."))]
        if not vision_names:
            raise RuntimeError("Cannot identify vision parameters to freeze; inspect model layout before FFT")
        for name, parameter in model.named_parameters():
            if name in vision_names:
                parameter.requires_grad_(False)

    rows = [{"prompt": [{"role": "system", "content": SYSTEM},
                         {"role": "user", "content": user_prompt(case["packet"])}],
             "completion": [{"role": "assistant", "content": json.dumps(case["expected"], separators=(",", ":"))}]}
            for case in cases]
    output = args.output.resolve()
    output.mkdir(parents=True)
    config = SFTConfig(
        output_dir=str(output / "checkpoints"), max_length=args.sequence_length,
        completion_only_loss=True, per_device_train_batch_size=1,
        gradient_accumulation_steps=4, max_steps=args.steps,
        learning_rate=2e-4 if args.mode == "lora" else 2e-5,
        save_strategy="steps", save_steps=args.steps, logging_steps=1,
        seed=3407, bf16=True, report_to="none", packing=False,
    )
    trainer = SFTTrainer(
        model=model, processing_class=tokenizer, train_dataset=Dataset.from_list(rows), args=config,
    )
    result = trainer.train()
    model.save_pretrained(str(output / "weights"))
    tokenizer.save_pretrained(str(output / "weights"))
    manifest = {
        "foundation": "Qwen/Qwen3.5-4B", "revision": args.revision,
        "mode": args.mode, "steps": args.steps, "sequence_length": args.sequence_length,
        "dataset_sha256": hashlib.sha256(args.train.read_bytes()).hexdigest(),
        "sample_count": len(cases), "seed": 3407, "training_loss": result.training_loss,
        "versions": {name: importlib.metadata.version(name) for name in
                     ("torch", "unsloth", "transformers", "trl", "datasets", "huggingface-hub")},
        "gpu": torch.cuda.get_device_name(),
    }
    (output / "run.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps({"run": str(output), "loss": result.training_loss}))


if __name__ == "__main__":
    main()
