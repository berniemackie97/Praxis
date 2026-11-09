#!/usr/bin/env python3
"""Reproducible catalog tradeoff probe, not a Praxis storage implementation."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import tempfile
from time import perf_counter

SCHEMA = Path(__file__).resolve().parents[2] / "src" / "Praxis.Storage" / "Catalog" / "Migrations" / "LocalCatalog.sql"


def connect(path):
    connection = sqlite3.connect(path)
    connection.execute("PRAGMA foreign_keys = ON")
    connection.execute("PRAGMA busy_timeout = 5000")
    mode = connection.execute("PRAGMA journal_mode = WAL").fetchone()[0]
    if mode != "wal":
        raise RuntimeError("SQLite WAL requires a supported local filesystem.")
    connection.execute("PRAGMA synchronous = FULL")
    return connection


def create_catalog(path):
    connection = connect(path)
    connection.executescript(SCHEMA.read_text(encoding="utf-8"))
    return connection


def fixture_row(index, revision=1):
    return {
        "source_id": f"source-{index:07d}",
        "item_id": f"item-{index:07d}",
        "kind": "file",
        "locator": f"fixture/nested/file-{index:07d}.txt",
        "digest": hashlib.sha256(f"content-{index % 128}-{revision}".encode()).hexdigest(),
        "revision": revision,
    }


def append_log(handle, row):
    handle.write(json.dumps(row, sort_keys=True, separators=(",", ":")) + "\n")


def catalog_probe(root, item_count, revisions):
    db_path, log_path = root / "catalog.sqlite", root / "catalog.jsonl"
    database = create_catalog(db_path)
    original = [fixture_row(i) for i in range(item_count)]
    started = perf_counter()
    with database:
        for row in original:
            database.execute("INSERT OR IGNORE INTO original_objects VALUES (?, ?, ?)",
                             (row["digest"], 100, "2026-11-08T00:00:00Z"))
            database.execute("INSERT INTO sources VALUES (?, ?, ?, ?, 'active')",
                             (row["source_id"], row["kind"], row["locator"],
                              "2026-11-08T00:00:00Z"))
            database.execute("INSERT INTO items VALUES (?, ?)",
                             (row["item_id"], row["source_id"]))
            database.execute("INSERT INTO item_versions VALUES (?, ?, ?, ?)",
                             (row["item_id"], row["revision"], row["digest"],
                              "2026-11-08T00:00:00Z"))
    sqlite_initial = perf_counter() - started

    started = perf_counter()
    with log_path.open("w", encoding="utf-8") as handle:
        for row in original:
            append_log(handle, row)
        handle.flush()
        os.fsync(handle.fileno())
    log_initial = perf_counter() - started

    started = perf_counter()
    for i in range(revisions):
        row = fixture_row(i % item_count, 2)
        with database:
            database.execute("INSERT OR IGNORE INTO original_objects VALUES (?, ?, ?)",
                             (row["digest"], 100, "2026-11-08T00:00:00Z"))
            database.execute("INSERT INTO item_versions VALUES (?, ?, ?, ?)",
                             (row["item_id"], 2, row["digest"],
                              "2026-11-08T00:00:00Z"))
    sqlite_updates = perf_counter() - started
    database.close()

    started = perf_counter()
    with log_path.open("a", encoding="utf-8") as handle:
        for i in range(revisions):
            append_log(handle, fixture_row(i % item_count, 2))
            handle.flush()
            os.fsync(handle.fileno())
    log_updates = perf_counter() - started

    started = perf_counter()
    database = connect(db_path)
    sqlite_count = database.execute("SELECT count(*) FROM sources").fetchone()[0]
    sqlite_revisions = database.execute("SELECT count(*) FROM item_versions").fetchone()[0]
    database.close()
    sqlite_reopen = perf_counter() - started

    started = perf_counter()
    snapshots = {}
    with log_path.open(encoding="utf-8") as handle:
        for line in handle:
            entry = json.loads(line)
            snapshots.setdefault(entry["source_id"], []).append(entry)
    log_reopen = perf_counter() - started
    if sqlite_count != len(snapshots) or sqlite_revisions != sum(map(len, snapshots.values())):
        raise AssertionError("Catalog candidates disagree on source identity or version count.")

    return {
        "items": item_count,
        "new_revisions": revisions,
        "sqlite_initial_s": round(sqlite_initial, 4),
        "jsonl_initial_s": round(log_initial, 4),
        "sqlite_revisions_s": round(sqlite_updates, 4),
        "jsonl_revisions_s": round(log_updates, 4),
        "sqlite_reopen_s": round(sqlite_reopen, 4),
        "jsonl_replay_s": round(log_reopen, 4),
        "sqlite_disk_bytes": sum(p.stat().st_size for p in root.glob("catalog.sqlite*")),
        "jsonl_disk_bytes": log_path.stat().st_size,
        "sqlite_version": sqlite3.sqlite_version,
    }


def streaming_probe(root, mib):
    payload = root / "large-original"
    chunk = b"0123456789abcdef" * 4096
    remaining = mib * 1024 * 1024
    digest = hashlib.sha256()
    with payload.open("xb") as handle:
        while remaining:
            part = chunk[:min(remaining, len(chunk))]
            handle.write(part)
            digest.update(part)
            remaining -= len(part)
        handle.flush()
        os.fsync(handle.fileno())
    verify = hashlib.sha256()
    with payload.open("rb") as handle:
        while data := handle.read(len(chunk)):
            verify.update(data)
    if digest.digest() != verify.digest() or payload.stat().st_size != mib * 1024 * 1024:
        raise AssertionError("Streaming probe failed original-byte integrity.")
    return {"original_bytes": payload.stat().st_size, "chunk_bytes": len(chunk)}


def sparse_size_probe(root, mib):
    size = mib * 1024 * 1024
    source = root / "sparse-original"
    with source.open("xb") as handle:
        handle.truncate(size)
    digest = hashlib.sha256()
    consumed = 0
    with source.open("rb") as handle:
        while data := handle.read(64 * 1024):
            if len(data) > 64 * 1024:
                raise AssertionError("The input read exceeded the bounded buffer.")
            digest.update(data)
            consumed += len(data)
    if consumed != size:
        raise AssertionError("The sparse input length changed during streaming.")
    return {"sparse_input_bytes_streamed": consumed, "sparse_sha256": digest.hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--items", type=int, default=4000)
    parser.add_argument("--revisions", type=int, default=100)
    parser.add_argument("--original-mib", type=int, default=64)
    parser.add_argument("--sparse-mib", type=int, default=2048)
    arguments = parser.parse_args()
    if not 1 <= arguments.items <= 100_000 or not 0 <= arguments.revisions <= arguments.items:
        parser.error("Require 1–100,000 items and 0–items revisions.")
    if not 0 <= arguments.original_mib <= 256:
        parser.error("Original fixture must be between 0 and 256 MiB.")
    if not 0 <= arguments.sparse_mib <= 4096:
        parser.error("Sparse input must be between 0 and 4096 MiB.")

    with tempfile.TemporaryDirectory(prefix="praxis-catalog-probe-") as directory:
        root = Path(directory)
        result = catalog_probe(root, arguments.items, arguments.revisions)
        result.update(streaming_probe(root, arguments.original_mib))
        result.update(sparse_size_probe(root, arguments.sparse_mib))
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
