#!/usr/bin/env python3
"""Smoke-test the published health-only API and its actual listener boundary."""

import os
from pathlib import Path
import socket
import subprocess
import sys
import time
from urllib.error import HTTPError, URLError
from urllib.request import urlopen


def assert_unpublished_routes(port):
    try:
        with urlopen(f"http://127.0.0.1:{port}/v1/library", timeout=1):
            raise AssertionError("A private API route unexpectedly responded.")
    except HTTPError as error:
        if error.code != 404 or error.headers.get("Access-Control-Allow-Origin"):
            raise AssertionError("An unregistered private route is exposed.") from error


def main():
    assembly = Path("artifacts/publish/Praxis.Api.dll").resolve()
    if not assembly.is_file():
        raise SystemExit(f"Published API is missing: {assembly}")

    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]

    environment = os.environ.copy()
    environment["PRAXIS_HEALTH_PORT"] = str(port)
    # Deliberately try to override the explicit Kestrel endpoint with a wildcard.
    environment["ASPNETCORE_URLS"] = f"http://0.0.0.0:{port}"
    environment["ASPNETCORE_ENVIRONMENT"] = "Production"
    process = subprocess.Popen(
        ["dotnet", str(assembly)],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        env=environment,
    )

    passed = False
    try:
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if process.poll() is not None:
                break
            try:
                with urlopen(f"http://127.0.0.1:{port}/health/live", timeout=1) as response:
                    live = response.status == 200 and response.read() == b"Healthy"
                with urlopen(f"http://127.0.0.1:{port}/health/ready", timeout=1) as response:
                    ready = response.status == 200 and response.read() == b"Ready"
                if live and ready:
                    passed = True
                    break
            except (OSError, URLError):
                pass
            time.sleep(0.25)

        if passed:
            assert_unpublished_routes(port)
            try:
                with socket.create_connection(("127.0.0.2", port), timeout=1):
                    raise AssertionError("Wildcard binding reached another loopback IPv4 address.")
            except OSError:
                pass
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
        raise SystemExit("Published API did not pass its loopback diagnostics smoke test.")

    invalid_environment = environment.copy()
    invalid_environment["PRAXIS_HEALTH_PORT"] = "0"
    invalid = subprocess.run(
        ["dotnet", str(assembly)],
        capture_output=True,
        timeout=10,
        env=invalid_environment,
        check=False,
    )
    if invalid.returncode == 0:
        raise SystemExit("Invalid health listener port unexpectedly started.")

    print("Published API: liveness and readiness passed, private routes absent, "
          "wildcard override rejected, invalid port rejected.")


if __name__ == "__main__":
    main()
