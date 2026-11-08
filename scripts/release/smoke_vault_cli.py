#!/usr/bin/env python3
"""Exercise the published local vault CLI against real files, without a network service."""

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

        invalid = root / "invalid.zip"
        invalid.write_bytes(b"invalid archive")
        run(executable, "verify", invalid, expected=1)
        run(executable, "restore", invalid, root / "invalid-restore", expected=1)
        if (root / "invalid-restore").exists():
            raise AssertionError("An invalid archive created a restore destination.")

    print("Vault CLI smoke passed: capture, verify, restore, byte fidelity, no overwrite and corruption.")


if __name__ == "__main__":
    main()
