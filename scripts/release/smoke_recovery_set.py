"""Exercise actual coordinated recovery of disposable catalog and identity state."""

import os
from pathlib import Path
import stat
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / "tools/Praxis.RecoveryCheck/bin/Release/net10.0/Praxis.RecoveryCheck.dll"
CREATED = b"Coordinated recovery rehearsal completed: "
BOUNDARY = b"PASS failure boundary: "
FAILURE = b"Praxis recovery check failed."
VERIFIED = b"Independent coordinated recovery verification passed."


def run(action, directory):
    return subprocess.run(
        ["dotnet", str(TOOL), action, str(directory)],
        capture_output=True, timeout=120, check=False,
    )


def require_success(result, action):
    if result.returncode != 0 or result.stderr:
        raise AssertionError(f"Coordinated recovery {action} failed (exit {result.returncode}).")


def verify_boundaries(output):
    expected = {
        b"Incorrect recovery secret",
        b"Insufficient budget",
        b"Active catalog lease",
        b"Existing restore destination",
        b"Cancellation",
        b"Missing security registry",
        b"Truncated registry",
    }
    if os.name != "nt":
        expected.add(b"Exposed backup permissions")
    observed = {line[len(BOUNDARY):] for line in output.splitlines()
                if line.startswith(BOUNDARY)}
    if observed != expected:
        raise AssertionError("Coordinated recovery did not exercise the expected failure boundaries.")


def assert_private_mode(path, expected):
    if os.name != "nt" and stat.S_IMODE(path.stat().st_mode) != expected:
        raise AssertionError("Recovery rehearsal files lack owner-private permissions.")


def main():
    if not TOOL.is_file():
        raise SystemExit("Build the Release recovery-check tool before the smoke test.")

    with tempfile.TemporaryDirectory(prefix="praxis-recovery-set-smoke-") as scratch:
        root = Path(scratch).resolve()
        assert_private_mode(root, 0o700)

        created = run("--set-create", root)
        require_success(created, "creation")
        verify_boundaries(created.stdout)
        reports = [line[len(CREATED):] for line in created.stdout.splitlines()
                   if line.startswith(CREATED)]
        if len(reports) != 1:
            raise AssertionError("Recovery did not report exactly one completed workspace.")

        workspace = Path(os.fsdecode(reports[0])).resolve(strict=True)
        if workspace.parent != root or not workspace.name.startswith("praxis-set-rehearsal-"):
            raise AssertionError("Recovery published an unexpected output directory.")
        assert_private_mode(workspace, 0o700)
        assert_private_mode(workspace / "evidence.json", 0o600)
        assert_private_mode(workspace / "backup" / "manifest.json", 0o600)

        verified = run("--set-verify", workspace)
        require_success(verified, "independent verification")
        if verified.stdout.strip() != VERIFIED:
            raise AssertionError("Independent recovery reported an unexpected result.")

        invalid = root / "private-location-must-not-leak"
        invalid.mkdir(mode=0o700)
        (invalid / "evidence.json").write_text("{", encoding="utf-8")
        if os.name != "nt":
            (invalid / "evidence.json").chmod(0o600)
        refused = run("--set-verify", invalid)
        if (refused.returncode != 1 or refused.stdout or
                refused.stderr.strip() != FAILURE):
            raise AssertionError("Malformed recovery evidence did not fail privately.")

    print("Coordinated recovery: owner-private snapshot, identity and original restore, "
          "failure boundaries, independent verification and redacted rejection passed.")


if __name__ == "__main__":
    main()
