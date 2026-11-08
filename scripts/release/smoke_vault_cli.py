#!/usr/bin/env python3
"""Exercise the published local vault CLI against real files, without a network service."""

import json
from pathlib import Path
import subprocess
import tempfile


def run(binary, *args, expected=0):
    result = subprocess.run(["dotnet", str(binary), *map(str, args)],
                            capture_output=True, text=True, timeout=30, check=False)
    if result.returncode != expected:
        raise AssertionError(f"CLI exit {result.returncode}, expected {expected}: "
                             f"{result.stdout} {result.stderr}")
    return result


def main():
    executable = Path("tools/Praxis.VaultCli/bin/Release/net10.0/Praxis.VaultCli.dll").resolve()
    if not executable.is_file():
        raise SystemExit(f"Vault CLI output is missing: {executable}")

    with tempfile.TemporaryDirectory(prefix="praxis-vault-smoke-") as temporary:
        root = Path(temporary)
        source = root / "source"
        (source / ".obsidian").mkdir(parents=True)
        (source / "Notes").mkdir()
        (source / "media").mkdir()
        contents = {
            "Notes/first.md": b"\xef\xbb\xbf# First\r\n![[../media/a.bin]]\r\n",
            "media/a.bin": b"\x00\x01\xff\x00",
            ".obsidian/app.json": b'{"theme":"dark"}'
        }
        for name, bytes_ in contents.items():
            (source / name).write_bytes(bytes_)

        archive = root / "saved.zip"
        destination = root / "recovered"
        run(executable, "capture", source, archive)
        verified = run(executable, "verify", archive)
        if "1 notes and 2 attachments" not in verified.stdout:
            raise AssertionError(f"Unexpected verification: {verified.stdout}")
        run(executable, "restore", archive, destination)
        for name, bytes_ in contents.items():
            actual = (destination / name).read_bytes()
            if actual != bytes_:
                raise AssertionError(f"Changed restored content: {name}")

        run(executable, "restore", archive, destination, expected=1)
        run(executable, "capture", source, archive, expected=1)
        if (destination / "Notes/first.md").read_bytes() != contents["Notes/first.md"]:
            raise AssertionError("A refused restore altered existing data.")

        (source / "Notes/first.md").write_bytes(b"# Updated\n![[../media/new.bin]]\n")
        (source / "media/a.bin").rename(source / "media/new.bin")
        next_archive = root / "updated.zip"
        run(executable, "plan", source, archive, expected=1)
        run(executable, "update", source, archive, next_archive, expected=1)
        if next_archive.exists():
            raise AssertionError("Ambiguous rename published a replacement archive.")

        mapping = root / "renames.json"
        mapping.write_text(json.dumps([{
            "fromPath": "media/a.bin",
            "toPath": "media/new.bin"
        }]), encoding="utf-8")
        preview = run(executable, "plan", source, archive, "--renames", mapping)
        if "1 updated, 1 renamed" not in preview.stdout:
            raise AssertionError(f"Unexpected incremental plan: {preview.stdout}")
        run(executable, "update", source, archive, next_archive, "--renames", mapping)
        verified = run(executable, "verify", next_archive)
        if "1 notes and 2 attachments" not in verified.stdout:
            raise AssertionError(f"Updated archive invalid: {verified.stdout}")

        updated_vault = root / "updated-restored"
        run(executable, "restore", next_archive, updated_vault)
        if (updated_vault / "Notes/first.md").read_bytes() != (
            source / "Notes/first.md").read_bytes():
            raise AssertionError("Updated Markdown did not restore exactly.")
        if (updated_vault / "media/new.bin").read_bytes() != contents["media/a.bin"]:
            raise AssertionError("Mapped attachment did not restore exactly.")
        if (updated_vault / ".obsidian/app.json").read_bytes() != contents[".obsidian/app.json"]:
            raise AssertionError("Hidden configuration was lost.")

        (source / ".obsidian/app.json").unlink()
        (source / ".obsidian").rmdir()
        removed_archive = root / "removed.zip"
        run(executable, "plan", source, next_archive, expected=1)
        run(executable, "update", source, next_archive, removed_archive, expected=1)
        if removed_archive.exists():
            raise AssertionError("A removed file was silently discarded.")

        invalid = root / "invalid.zip"
        invalid.write_bytes(b"invalid archive")
        run(executable, "verify", invalid, expected=1)
        run(executable, "restore", invalid, root / "invalid-restore", expected=1)
        if (root / "invalid-restore").exists():
            raise AssertionError("An invalid archive created a restore destination.")

    print("Vault CLI smoke passed: capture, verify, incremental plan/update, explicit rename, restore, no overwrite, missing-file and corruption checks.")


if __name__ == "__main__":
    main()
