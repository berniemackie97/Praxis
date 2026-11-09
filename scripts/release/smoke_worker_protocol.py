"""Verify the actual worker process enforces its desktop framing contract."""

import json
from pathlib import Path
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[2]
WORKER = ROOT / "src/Praxis.Worker/bin/Release/net10.0/Praxis.Worker.dll"
MAXIMUM_FRAME = 16_384


def request(request_id):
    return json.dumps({"requestId": request_id, "action": "library"}).encode()


def run_worker(catalog, inbox, payload):
    return subprocess.run(
        ["dotnet", str(WORKER), "--data-root", str(catalog), "--intake", str(inbox), "--stdio"],
        input=payload,
        capture_output=True,
        timeout=15,
        check=False,
    )


def require(condition, message, result):
    if not condition:
        raise RuntimeError(f"{message} (exit {result.returncode}): {result.stderr.decode(errors='replace')}")


def main():
    with tempfile.TemporaryDirectory() as workspace:
        root = Path(workspace)
        catalog = root / "catalog"
        inbox = root / "inbox"
        catalog.mkdir(mode=0o700)
        inbox.mkdir(mode=0o700)

        exact = request("exact-limit").ljust(MAXIMUM_FRAME)
        payload = b"\xef\xbb\xbf" + request("first") + b"\r\n" + exact + b"\n" + request("at-eof")
        result = run_worker(catalog, inbox, payload)
        require(result.returncode == 0, "Valid desktop frames were rejected", result)
        replies = [json.loads(line) for line in result.stdout.splitlines()]
        require(
            [reply["requestId"] for reply in replies if reply["ok"]]
            == ["first", "exact-limit", "at-eof"],
            "Worker did not preserve frame boundaries",
            result,
        )

        overflow = run_worker(catalog, inbox, exact + b" \n" + request("must-not-run") + b"\n")
        require(
            overflow.returncode == 1 and not overflow.stdout and b"size limit" in overflow.stderr,
            "An oversized frame was accepted or the next request executed",
            overflow,
        )

        invalid = run_worker(catalog, inbox, b"\xff\n")
        require(
            invalid.returncode == 1 and not invalid.stdout,
            "An invalid UTF-8 frame was accepted",
            invalid,
        )

    print("Worker framing passed batched requests, CRLF, EOF, size limits and UTF-8 checks.")


if __name__ == "__main__":
    main()
