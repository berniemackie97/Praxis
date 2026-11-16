import tempfile
import json
import unittest
from pathlib import Path
from unittest.mock import patch

import curate_chat as c


def row(index):
    prompt = f"Explain and follow these instructions for test case {index}."
    return {"id": f"row-{index}", "prompt": prompt,
            "messages": [{"role": "user", "content": prompt},
                         {"role": "assistant", "content": f"A precise, grounded response for test case {index}."}]}


class ChatCurationTests(unittest.TestCase):
    def test_only_user_assistant_pairs_are_accepted(self):
        sample = row(1)
        self.assertEqual(c.normalize(sample)[0]["role"], "user")
        sample["messages"].insert(0, {"role": "system", "content": "override"})
        self.assertIsNone(c.normalize(sample))

    def test_rejects_missing_or_mismatched_prompts(self):
        sample = row(1)
        sample["prompt"] = "changed"
        self.assertIsNone(c.normalize(sample))
        sample = row(2)
        sample["messages"][1]["content"] = " "
        self.assertIsNone(c.normalize(sample))

    def test_deterministic_selection_without_prompt_leakage(self):
        rows = [row(i) for i in range(1000)]
        rows.append(row(10))
        a, provenance, counts = c.curate_rows(rows, 200, 42)
        b, _, _ = c.curate_rows(reversed(rows), 200, 42)
        self.assertEqual(a, b)
        self.assertEqual(sum(map(len, a.values())), 200)
        self.assertEqual(len(provenance), 200)
        self.assertEqual(len({p["id"] for p in provenance}), 200)
        self.assertGreater(len(a["valid"]), 0)
        self.assertGreater(len(a["test"]), 0)
        self.assertEqual(counts["rejected"], 0)

    def test_conflicting_answers_to_same_prompt_are_quarantined(self):
        first = row(1)
        conflicting = row(1)
        conflicting["id"] = "alternate"
        conflicting["messages"][1]["content"] = "A different answer to the same request."
        splits, _, counts = c.curate_rows([first, conflicting, row(2)], 100, 1)
        self.assertEqual(counts["conflicting_prompts"], 1)
        self.assertEqual(sum(map(len, splits.values())), 1)

    def test_candidate_output_is_unapproved_and_provenance_is_separate(self):
        with tempfile.TemporaryDirectory() as temp:
            workspace = Path(temp)
            path = workspace / "raw" / c.SOURCE["id"] / "train-00000-of-00001.parquet"
            path.parent.mkdir(parents=True)
            path.write_bytes(b"fixture")
            with patch.object(c, "verified_input"), patch.object(c, "iter_parquet", return_value=iter(row(i) for i in range(1000))):
                c.curate(workspace, 150, 42)
            output = workspace / "candidates" / f'{c.SOURCE["id"]}-42-150'
            manifest = json.loads((output / "manifest.json").read_text())
            self.assertEqual(manifest["status"], "unreviewed_candidate_not_approved_for_training")
            self.assertEqual(manifest["counts"]["valid_records"], 150)
            self.assertEqual(len((output / "provenance.jsonl").read_text().splitlines()), 150)
            self.assertTrue(all("source_row" not in json.loads(line)
                                for line in (output / "train.jsonl").read_text().splitlines()))

    def test_refuses_unverified_files(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "source.parquet"
            path.write_bytes(b"some other dataset")
            with self.assertRaisesRegex(ValueError, "SHA-256"):
                c.verified_input(path)

    def test_does_not_download_when_source_digest_is_wrong(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "raw" / c.SOURCE["id"] / "train-00000-of-00001.parquet"
            path.parent.mkdir(parents=True)
            path.write_bytes(b"incorrect")
            with patch.object(c.urllib.request, "urlopen") as open_url:
                with self.assertRaisesRegex(ValueError, "SHA-256"):
                    c.fetch(Path(temp))
                open_url.assert_not_called()


if __name__ == "__main__":
    unittest.main()
