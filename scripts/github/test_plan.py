import copy
import json
import unittest
from pathlib import Path

from plan import validate


class RoadmapValidationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.plan = json.loads(
            Path(__file__).with_name("roadmap.json").read_text(encoding="utf-8")
        )

    def test_complete_roadmap(self):
        items, dependencies = validate(self.plan)

        self.assertEqual(59, len(items))
        self.assertEqual(8, len(self.plan["milestones"]))
        self.assertEqual(14, len(self.plan["features"]))
        self.assertEqual(1, items[2]["parent"])
        self.assertEqual(3, items[54]["parent"])
        self.assertEqual(54, items[14]["parent"])
        self.assertEqual({15, 17, 40, 41}, dependencies[19])

    def test_duplicate_child_is_rejected(self):
        data = copy.deepcopy(self.plan)
        data["features"][0]["children"].append(14)

        with self.assertRaisesRegex(ValueError, "Duplicate work item"):
            validate(data)

    def test_unknown_dependency_is_rejected(self):
        data = copy.deepcopy(self.plan)
        data["dependencies"]["16"].append(9999)

        with self.assertRaisesRegex(ValueError, "Invalid dependency"):
            validate(data)

    def test_dependency_cycle_is_rejected(self):
        data = copy.deepcopy(self.plan)
        data["dependencies"]["13"].append(15)

        with self.assertRaisesRegex(ValueError, "Cyclic issue dependencies"):
            validate(data)

    def test_missing_milestone_is_rejected(self):
        data = copy.deepcopy(self.plan)
        data["milestones"].pop()

        with self.assertRaisesRegex(ValueError, "eight distinct milestone"):
            validate(data)

    def test_invalid_priority_is_rejected(self):
        data = copy.deepcopy(self.plan)
        data["priority_overrides"]["50"] = "P9"

        with self.assertRaisesRegex(ValueError, "Invalid priority override"):
            validate(data)


if __name__ == "__main__":
    unittest.main()
