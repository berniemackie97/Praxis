"""Published worker checks for restart, exclusive catalog access and private stderr."""

import json
import select
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


def assert_private_failure(result):
    if result.returncode != 1 or result.stdout or SENTINEL.encode() in result.stderr:
        raise AssertionError(
            f"Worker failure was not private and nonzero: exit={result.returncode}"
        )
    if b"Praxis worker failed (" not in result.stderr:
        raise AssertionError("Worker did not report a stable failure category.")


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
        assert_private_failure(missing)

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
            assert_private_failure(contended)

            first.stdin.close()
            if first.wait(timeout=15) != 0:
                raise AssertionError("Normal desktop pipe EOF did not shut down cleanly.")
            if SENTINEL.encode() in first.stderr.read():
                raise AssertionError("Worker stderr exposed an approved private path.")
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

    print("Worker lifecycle: private failures, exclusive lock, clean EOF and restart passed.")


if __name__ == "__main__":
    main()
