"""Exercise recovery commands against a catalog populated by the real worker."""

import json
from pathlib import Path
import subprocess
import tempfile
from uuid import uuid4

from smoke_worker_protocol import WORKER, require, run_worker


def recover(action, *paths, max_bytes=10_000_000):
    return subprocess.run(
        ["dotnet", str(WORKER), "recovery", action, *map(str, paths), "--max-bytes", str(max_bytes)],
        capture_output=True,
        timeout=15,
        check=False,
    )


def desktop(catalog, inbox, action, **fields):
    request = json.dumps({"requestId": "smoke", "action": action, **fields}).encode() + b"\n"
    result = run_worker(catalog, inbox, request)
    require(result.returncode == 0, "Desktop worker failed", result)
    response = json.loads(result.stdout)
    require(response["ok"], f"Desktop request failed: {response.get('error')}", result)
    return response["data"]


def main():
    with tempfile.TemporaryDirectory() as workspace:
        root = Path(workspace)
        catalog = root / "catalog"
        inbox = root / "inbox"
        backup = root / "backup"
        restored = root / "restored"
        catalog.mkdir(mode=0o700)
        inbox.mkdir(mode=0o700)

        missing = recover("backup", catalog, backup)
        require(missing.returncode == 1 and not list(catalog.iterdir()) and not backup.exists(),
                "Backup initialized a missing catalog", missing)

        first = desktop(catalog, inbox, "noteCapture", captureId=str(uuid4()),
                        title="Recovery fixture", text="A preserved original.")
        original = first["capture"]["original"]
        backup_result = recover("backup", catalog, backup)
        require(backup_result.returncode == 0, "Backup failed", backup_result)
        report = json.loads(backup_result.stdout)
        require(report["ObjectCount"] == 1, "Unexpected backup inventory", backup_result)

        verified = recover("verify", backup)
        require(verified.returncode == 0 and json.loads(verified.stdout) == report,
                "Independent verification failed", verified)

        desktop(catalog, inbox, "noteCapture", captureId=str(uuid4()),
                title="After snapshot", text="Only present in the live catalog.")
        recovered = recover("restore", backup, restored)
        require(recovered.returncode == 0 and json.loads(recovered.stdout) == report,
                "Restore failed", recovered)
        recovered_items = desktop(restored, root / "offline-inbox", "library")["items"]
        require([item["itemId"] for item in recovered_items] == [original["itemId"]],
                "Restore did not preserve the snapshot's source identity", recovered)
        digest = original["sha256"]
        restored_file = restored / "objects" / digest[:2] / digest[2:4] / digest
        require(restored_file.read_bytes() == Path(first["capture"]["sourcePath"]).read_bytes(),
                "Restore changed the original bytes", recovered)

        existing = recover("restore", backup, restored)
        require(existing.returncode == 1 and not existing.stdout,
                "Restore accepted an existing destination", existing)
        low_budget = recover("verify", backup, max_bytes=1)
        require(low_budget.returncode == 1 and not low_budget.stdout and
                low_budget.stderr.strip() == b"Praxis worker failed (invalid_input).",
                "Verification accepted a snapshot above its budget", low_budget)

        invalid = recover("verify", backup, max_bytes=0)
        require(invalid.returncode == 2 and not invalid.stdout, "Invalid byte limit was accepted", invalid)

        backup_file = backup / "objects" / digest[:2] / digest[2:4] / digest
        content = backup_file.read_bytes()
        backup_file.write_bytes(bytes([content[0] ^ 1]) + content[1:])
        corrupt_destination = root / "corrupt-restore"
        corrupt = recover("restore", backup, corrupt_destination)
        require(corrupt.returncode == 1 and not corrupt.stdout and not corrupt_destination.exists(),
                "Restore accepted a corrupted original", corrupt)

    print("Catalog recovery passed backup, independent verification, exact restore and refusal paths.")


if __name__ == "__main__":
    main()
