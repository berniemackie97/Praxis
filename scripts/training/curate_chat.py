#!/usr/bin/env python3
"""Stage a provenance-checked Praxis Chat SFT candidate; never promotes training data."""

import argparse
import hashlib
import json
import os
import shutil
import tempfile
from pathlib import Path
import urllib.request

SOURCE = {
    "id": "tulu3-personas",
    "repository": "allenai/tulu-3-sft-personas-instruction-following",
    "revision": "76c414d",
    "path": "data/train-00000-of-00001.parquet",
    "sha256": "19a16c5f1649d367f69899b3cfadbbeb5ffef91f24e20c6617588bdd87cd3e60",
    "license": "odc-by",
    "max_bytes": 50_000_000,
}


def sha256_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verified_input(path):
    if not path.is_file():
        raise ValueError(f"Missing source file: {path}")
    if path.stat().st_size > SOURCE["max_bytes"]:
        raise ValueError("Source exceeds allowed size")
    if sha256_file(path) != SOURCE["sha256"]:
        raise ValueError("Source SHA-256 differs from the pinned safe file")


def fetch(workspace):
    target = workspace / "raw" / SOURCE["id"] / Path(SOURCE["path"]).name
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        verified_input(target)
        return target

    url = (f'https://huggingface.co/datasets/{SOURCE["repository"]}'
           f'/resolve/{SOURCE["revision"]}/{SOURCE["path"]}')
    request = urllib.request.Request(url, headers={"User-Agent": "Praxis-chat-data-curation/1"})
    temporary = None
    digest = hashlib.sha256()
    size = 0
    try:
        with urllib.request.urlopen(request, timeout=60) as remote:
            if not remote.url.startswith("https://"):
                raise ValueError("Refusing a non-HTTPS download")
            with tempfile.NamedTemporaryFile(mode="wb", dir=target.parent,
                                             prefix=f".{target.name}-", delete=False) as output:
                temporary = Path(output.name)
                while chunk := remote.read(1 << 20):
                    size += len(chunk)
                    if size > SOURCE["max_bytes"]:
                        raise ValueError("Download exceeds allowed size")
                    output.write(chunk)
                    digest.update(chunk)
        if digest.hexdigest() != SOURCE["sha256"]:
            raise ValueError("Downloaded bytes do not match the reviewed SHA-256")
        os.replace(temporary, target)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
    return target


def normalize(row):
    messages = row.get("messages")
    if not isinstance(messages, list) or len(messages) != 2:
        return None
    if not all(isinstance(message, dict) for message in messages):
        return None
    if [message.get("role") for message in messages] != ["user", "assistant"]:
        return None
    prompt, response = [message.get("content") for message in messages]
    if not all(isinstance(text, str) and 20 <= len(text.strip()) <= 10_000 for text in (prompt, response)):
        return None
    if row.get("prompt") != prompt:
        return None
    return [{"role": "user", "content": prompt.strip()},
            {"role": "assistant", "content": response.strip()}]


def curate_rows(rows, limit, seed):
    unique = {}
    conflicts = set()
    rejected = duplicates = total = 0
    for row in rows:
        total += 1
        messages = normalize(row) if isinstance(row, dict) else None
        if messages is None or not isinstance(row.get("id"), str):
            rejected += 1
            continue
        key = hashlib.sha256(messages[0]["content"].encode("utf-8")).hexdigest()
        if key in conflicts:
            continue
        existing = unique.get(key)
        if existing is not None:
            if existing["messages"] != messages:
                del unique[key]
                conflicts.add(key)
            else:
                duplicates += 1
            continue
        unique[key] = {"messages": messages, "source_row": row["id"]}
    candidates = sorted(unique.items(), key=lambda item: hashlib.sha256(
        f"rank:{seed}:{item[0]}".encode()).digest())[:limit]
    splits = {"train": [], "valid": [], "test": []}
    provenance = []
    for key, record in candidates:
        bucket = int.from_bytes(hashlib.sha256(f"split:{key}".encode()).digest()[:8], "big") % 100
        split = "test" if bucket < 5 else "valid" if bucket < 10 else "train"
        splits[split].append({"messages": record["messages"]})
        provenance.append({"id": key, "source_row": record["source_row"], "split": split})
    return splits, provenance, {"read": total, "rejected": rejected,
                                 "duplicate_prompts": duplicates,
                                 "conflicting_prompts": len(conflicts)}


def iter_parquet(path):
    try:
        import pyarrow.parquet as parquet
    except ImportError as exc:
        raise RuntimeError("Parquet curation requires pyarrow in the training environment") from exc
    reader = parquet.ParquetFile(path)
    for batch in reader.iter_batches(batch_size=256, columns=["id", "prompt", "messages"]):
        yield from batch.to_pylist()


def write_jsonl(path, records):
    with path.open("w", encoding="utf-8", newline="\n") as stream:
        for record in records:
            stream.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")


def curate(workspace, limit, seed):
    source = workspace / "raw" / SOURCE["id"] / Path(SOURCE["path"]).name
    verified_input(source)
    splits, provenance, counts = curate_rows(iter_parquet(source), limit, seed)
    if not splits["train"] or not splits["valid"] or not splits["test"]:
        raise ValueError("Candidate requires nonempty train, validation, and test splits")
    output = workspace / "candidates" / f'{SOURCE["id"]}-{seed}-{limit}'
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists() or output.is_symlink():
        raise FileExistsError(f"Candidate already exists: {output}")
    staging = Path(tempfile.mkdtemp(prefix=f".{output.name}-", dir=output.parent))
    try:
        for split, records in splits.items():
            write_jsonl(staging / f"{split}.jsonl", records)
        write_jsonl(staging / "provenance.jsonl", provenance)
        manifest = {
            "status": "unreviewed_candidate_not_approved_for_training",
            "source": {key: value for key, value in SOURCE.items() if key != "max_bytes"},
            "seed": seed,
            "limit": limit,
            "counts": {"valid_records": sum(map(len, splits.values())),
                       "train": len(splits["train"]), "valid": len(splits["valid"]),
                       "test": len(splits["test"]), "read": counts["read"],
                       "rejected_schema": counts["rejected"],
                       "duplicate_prompts": counts["duplicate_prompts"],
                       "conflicting_prompts": counts["conflicting_prompts"]},
            "files_sha256": {p.name: sha256_file(p) for p in sorted(staging.glob("*.jsonl"))},
        }
        (staging / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        if output.exists() or output.is_symlink():
            raise FileExistsError(f"Candidate already exists: {output}")
        os.rename(staging, output)
    finally:
        if staging.exists():
            shutil.rmtree(staging)
    print(json.dumps({"candidate": str(output), "counts": manifest["counts"]}, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["fetch", "curate"])
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=2_000)
    parser.add_argument("--seed", type=int, default=20261116)
    args = parser.parse_args()
    if args.limit < 100:
        parser.error("--limit must be at least 100")
    if args.command == "fetch":
        print(fetch(args.workspace))
    else:
        curate(args.workspace, args.limit, args.seed)


if __name__ == "__main__":
    main()
