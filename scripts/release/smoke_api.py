#!/usr/bin/env python3
"""Smoke-test the published Praxis API as a separate loopback process."""

import os
from pathlib import Path
import socket
import subprocess
import sys
import time
from urllib.error import URLError
from urllib.request import urlopen


def main():
    assembly = Path("artifacts/publish/Praxis.Api.dll").resolve()
    if not assembly.is_file():
        raise SystemExit(f"Published API is missing: {assembly}")

    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]

    environment = os.environ.copy()
    environment["ASPNETCORE_URLS"] = f"http://127.0.0.1:{port}"
    environment["ASPNETCORE_ENVIRONMENT"] = "Production"
    process = subprocess.Popen(
        ["dotnet", str(assembly)],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        env=environment
    )
    passed = False
    try:
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if process.poll() is not None:
                break
            try:
                with urlopen(f"http://127.0.0.1:{port}/health/live", timeout=1) as response:
                    if response.status == 200 and response.read() == b"Healthy":
                        passed = True
                        break
            except (OSError, URLError):
                pass
            time.sleep(0.25)
    finally:
        if process.poll() is None:
            process.terminate()
        try:
            output, _ = process.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            output, _ = process.communicate()

    if not passed:
        print(output.decode("utf-8", errors="replace")[-4000:], file=sys.stderr)
        raise SystemExit("Published API did not pass its loopback liveness smoke test.")
    print("Published Praxis API started and returned HTTP 200 Healthy on loopback.")


if __name__ == "__main__":
    main()
