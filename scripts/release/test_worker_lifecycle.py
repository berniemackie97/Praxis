"""Unit-level checks for owner-local worker log and failure evidence parsing."""

import subprocess
import unittest

from smoke_worker_lifecycle import SENTINEL, assert_lifecycle_logs, assert_private_failure


RUN = b"a" * 32


def event(sequence, action, run=RUN):
    return (b"[2026-11-10T20:00:00.0000000+00:00] run=" + run +
            f" seq={sequence} Worker supervisor {action}.\n".encode())


class WorkerOperationalEvidenceTests(unittest.TestCase):
    def test_correlated_lifecycle_accepts_interleaved_events(self):
        lines = (event(1, "started") +
                 b"[2026-11-10T20:00:00.1000000+00:00] run=" + RUN +
                 b" seq=2 Watched directory is offline.\n" +
                 event(3, "stopped"))
        self.assertEqual(RUN, assert_lifecycle_logs(lines))

    def test_lifecycle_refuses_changed_run_or_nonmonotonic_order(self):
        for lines in (
            event(1, "started") + event(2, "stopped", b"b" * 32),
            event(2, "started") + event(1, "stopped"),
            event(1, "stopped") + event(2, "started"),
            event(1, "started"),
        ):
            with self.subTest(lines=lines), self.assertRaises(AssertionError):
                assert_lifecycle_logs(lines)

    def test_failure_category_and_private_diagnostics_are_enforced(self):
        accepted = subprocess.CompletedProcess([], 1, b"",
                                               b"Praxis worker failed (unsupported).\n")
        assert_private_failure(accepted, "unsupported")
        for failed in (
            subprocess.CompletedProcess([], 0, b"", accepted.stderr),
            subprocess.CompletedProcess([], 1, b"private response", accepted.stderr),
            subprocess.CompletedProcess([], 1, b"", b"Praxis worker failed (io_error).\n"),
            subprocess.CompletedProcess([], 1, b"",
                                        accepted.stderr + SENTINEL.encode()),
        ):
            with self.subTest(result=failed), self.assertRaises(AssertionError):
                assert_private_failure(failed, "unsupported")


if __name__ == "__main__":
    unittest.main()
