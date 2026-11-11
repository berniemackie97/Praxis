"""Published worker checks for restart, exclusive catalog access and private stderr."""

import json
import os
import re
import select
import sqlite3
import subprocess
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
WORKER = ROOT / "src/Praxis.Worker/bin/Release/net10.0/Praxis.Worker.dll"
SENTINEL = "private-path-must-not-appear-in-diagnostics"


def command(catalog, inbox):
    return [
        "dotnet", str(WORKER), "--data-root", str(catalog),
        "--intake", str(inbox), "--stdio",
    ]


def request():
    return (json.dumps({"requestId": "probe", "action": "library"}) + "\n").encode()


def run(catalog, inbox):
    return subprocess.run(
        command(catalog, inbox), input=request(),
        capture_output=True, timeout=15, check=False,
    )


def assert_private_failure(result, category):
    expected = f"Praxis worker failed ({category}).".encode()
    if result.returncode != 1 or result.stdout or SENTINEL.encode() in result.stderr:
        raise AssertionError(
            f"Worker failure was not private and nonzero: exit={result.returncode}"
        )
    if expected not in result.stderr:
        raise AssertionError(f"Worker did not report the expected {category} failure.")


def assert_lifecycle_logs(stderr):
    events = re.findall(
        rb"(?m)^\[[^\]\r\n]+\] run=([0-9a-f]{32}) seq=(\d+) Worker supervisor (started|stopped)\.$",
        stderr,
    )
    if (len(events) != 2 or events[0][2] != b"started" or
            events[1][2] != b"stopped" or events[0][0] != events[1][0] or
            int(events[0][1]) >= int(events[1][1])):
        raise AssertionError("Worker lifecycle logs lack ordered per-run correlation.")
    return events[0][0]


def main():
    if not WORKER.is_file():
        raise SystemExit("Build the Release worker before the lifecycle smoke test.")

    with tempfile.TemporaryDirectory() as workspace:
        root = Path(workspace)
        catalog = root / "catalog"
        inbox = root / SENTINEL
        catalog.mkdir(mode=0o700)
        inbox.mkdir(mode=0o700)

        missing = run(root / (SENTINEL + "-missing"), inbox)
        assert_private_failure(missing, "directory_unavailable")

        first = subprocess.Popen(
            command(catalog, inbox),
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        )
        try:
            first.stdin.write(request())
            first.stdin.flush()
            readable, _, _ = select.select([first.stdout], [], [], 15)
            if not readable:
                raise AssertionError("The supervised worker did not respond to its first request.")
            response = json.loads(first.stdout.readline())
            if not response["ok"] or response["requestId"] != "probe":
                raise AssertionError("The first worker did not respond before shutdown.")

            contended = run(catalog, inbox)
            assert_private_failure(contended, "io_error")

            first.stdin.close()
            if first.wait(timeout=15) != 0:
                raise AssertionError("Normal desktop pipe EOF did not shut down cleanly.")
            first_logs = first.stderr.read()
            if SENTINEL.encode() in first_logs:
                raise AssertionError("Worker stderr exposed an approved private path.")
            first_run_id = assert_lifecycle_logs(first_logs)
        finally:
            if first.poll() is None:
                first.kill()
                first.wait(timeout=5)
            if first.stdin and not first.stdin.closed:
                first.stdin.close()
            first.stdout.close()
            first.stderr.close()

        restarted = run(catalog, inbox)
        if restarted.returncode != 0:
            raise AssertionError("The worker could not reopen its catalog after shutdown.")
        response = json.loads(restarted.stdout)
        if not response["ok"] or response["requestId"] != "probe":
            raise AssertionError("Restarted worker returned an invalid response.")
        if SENTINEL.encode() in restarted.stderr:
            raise AssertionError("Worker restart emitted a private path.")
        if assert_lifecycle_logs(restarted.stderr) == first_run_id:
            raise AssertionError("Worker restart reused the previous run's correlation ID.")

        unexpected = root / (SENTINEL + "-unexpected")
        unexpected.write_text("unapproved", encoding="utf-8")
        try:
            assert_private_failure(run(catalog, inbox), "invalid_input")
        finally:
            unexpected.unlink()

        if os.name != "nt":
            original_mode = catalog.stat().st_mode & 0o777
            catalog.chmod(0o755)
            try:
                assert_private_failure(run(catalog, inbox), "access_denied")
            finally:
                catalog.chmod(original_mode)

            linked = root / (SENTINEL + "-linked")
            linked.symlink_to(catalog, target_is_directory=True)
            try:
                assert_private_failure(run(linked, inbox), "invalid_input")
            finally:
                linked.unlink()

        damaged = root / (SENTINEL + "-damaged")
        damaged.mkdir(mode=0o700)
        (damaged / "catalog.sqlite").write_bytes(b"not a valid sqlite database")
        assert_private_failure(run(damaged, inbox), "storage_error")

        with sqlite3.connect(catalog / "catalog.sqlite") as database:
            database.execute("PRAGMA user_version = 17")
        assert_private_failure(run(catalog, inbox), "unsupported")
        with sqlite3.connect(catalog / "catalog.sqlite") as database:
            if database.execute("PRAGMA user_version").fetchone()[0] != 17:
                raise AssertionError("An unsupported catalog version was modified.")

    print("Worker lifecycle: private failures, lock, EOF/restart, correlated logs, "
          "permissions, incompatible schema and corrupt storage passed.")


if __name__ == "__main__":
    main()
