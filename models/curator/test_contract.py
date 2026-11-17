import json
from pathlib import Path
import unittest

from contract import load_cases, score, valid_response


ROOT = Path(__file__).parent


class ContractTests(unittest.TestCase):
    def test_seeds_have_disjoint_groups_and_unique_ids(self):
        train = load_cases(ROOT / "cases/train.jsonl")
        evaluation = load_cases(ROOT / "cases/eval.jsonl")
        self.assertFalse({case["group"] for case in train} & {case["group"] for case in evaluation})
        self.assertFalse({case["id"] for case in train} & {case["id"] for case in evaluation})

    def test_gold_scores_perfectly(self):
        cases = load_cases(ROOT / "cases/eval.jsonl")
        report = score(cases, {case["id"]: case["expected"] for case in cases})
        self.assertEqual(report["strict_correct"], len(cases))
        self.assertEqual(report["unsafe_changes"], 0)

    def test_revoked_evidence_rejected(self):
        packet = {"records": [{"id": "a"}],
                  "evidence": [{"id": "revoked", "eligible": False}]}
        response = {"decision": "keep", "target_id": "a", "related_id": None,
                    "evidence_ids": ["revoked"], "reason": "No change"}
        with self.assertRaisesRegex(ValueError, "eligible evidence"):
            valid_response(response, packet)

    def test_false_merge_detected_as_unsafe(self):
        cases = load_cases(ROOT / "cases/eval.jsonl")
        no_change = next(c for c in cases if c["expected"]["decision"] == "keep_separate")
        wrong = dict(no_change["expected"], decision="merge_entities")
        report = score([no_change], {no_change["id"]: wrong})
        self.assertEqual(report["unsafe_changes"], 1)


if __name__ == "__main__":
    unittest.main()
