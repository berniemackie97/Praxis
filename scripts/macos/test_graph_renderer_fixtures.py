"""Fixture shape and determinism checks; no macOS runtime required."""

import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("graph_renderer_fixtures.py")
spec = importlib.util.spec_from_file_location("graph_renderer_fixtures", MODULE_PATH)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class GraphFixtureTests(unittest.TestCase):
    def test_topologies(self):
        expected = {
            "sparse": (32, 12, 1),
            "connected": (200, 400, 4),
            "clustered": (800, 1607, 6),
            "high_degree": (1500, 2697, 1199),
        }
        for name, counts in expected.items():
            with self.subTest(name=name):
                fixture = module.make_case(name)
                self.assertEqual(counts, (fixture["stats"]["nodeCount"],
                                          fixture["stats"]["edgeCount"],
                                          fixture["stats"]["maxDegree"]))
                ids = {node["id"] for node in fixture["nodes"]}
                self.assertEqual(len(ids), counts[0])
                self.assertEqual(len({edge["id"] for edge in fixture["edges"]}), counts[1])
                for edge in fixture["edges"]:
                    self.assertIn(edge["sourceId"], ids)
                    self.assertIn(edge["targetId"], ids)
                    self.assertNotEqual(edge["sourceId"], edge["targetId"])

    def test_file_generation_is_repeatable_and_non_overwriting(self):
        with tempfile.TemporaryDirectory() as root:
            first, second = Path(root) / "first", Path(root) / "second"
            for path in (first, second):
                subprocess.run([sys.executable, str(MODULE_PATH), str(path)], check=True,
                               capture_output=True, text=True)
            for name in ("sparse", "connected", "clustered", "high_degree"):
                self.assertEqual((first / f"{name}.json").read_bytes(),
                                 (second / f"{name}.json").read_bytes())
            failed = subprocess.run([sys.executable, str(MODULE_PATH), str(first)],
                                    capture_output=True, text=True)
            self.assertNotEqual(0, failed.returncode)


if __name__ == "__main__":
    unittest.main()
