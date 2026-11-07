import copy
import json
import unittest
from unittest.mock import patch
from pathlib import Path

from plan import blocked_by_numbers, reconcile_issues, reconcile_project, validate


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


class ReconciliationTests(unittest.TestCase):
    def test_blocking_relationships_use_nodes_not_mapping_keys(self):
        issue = {
            "number": 19,
            "blockedBy": {"nodes": [{"number": 15}, {"number": 17}], "totalCount": 2},
        }
        self.assertEqual({15, 17}, blocked_by_numbers(issue))

    def test_no_blocking_relationships(self):
        issue = {"number": 19, "blockedBy": {"nodes": [], "totalCount": 0}}
        self.assertEqual(set(), blocked_by_numbers(issue))

    def test_partial_blocking_relationships_stop_reconciliation(self):
        issue = {"number": 19, "blockedBy": {"nodes": [{"number": 15}], "totalCount": 2}}
        with self.assertRaisesRegex(RuntimeError, "Refusing incomplete reconciliation"):
            blocked_by_numbers(issue)

    @patch("plan.gh")
    @patch("plan.gh_json")
    def test_existing_blocker_is_not_added_again(self, fetch, execute):
        issue = {
            "number": 19,
            "title": "[Task] Authenticated sync",
            "labels": [{"name": "kind:task"}, {"name": "area:sync"}],
            "milestone": {"title": "M2"},
            "parent": {"number": 56},
            "blockedBy": {"nodes": [{"number": 15}], "totalCount": 1},
        }
        fetch.return_value = [issue]
        plan = {"repository": "berniemackie97/Praxis",
                "milestones": [{"stage": "M2", "title": "M2"}]}
        items = {19: {"kind": None, "stage": "M2", "area": "sync", "parent": 56}}
        reconcile_issues(plan, items, {19: {15, 17}})

        execute.assert_called_once_with(
            "issue", "edit", 19, "-R", "berniemackie97/Praxis",
            "--add-blocked-by", 17
        )

    @patch("plan.gh")
    @patch("plan.gh_json")
    def test_partial_blockers_never_trigger_a_write(self, fetch, execute):
        fetch.return_value = [{
            "number": 19, "title": "[Task] Authenticated sync",
            "labels": [{"name": "kind:task"}, {"name": "area:sync"}],
            "milestone": {"title": "M2"}, "parent": {"number": 56},
            "blockedBy": {"nodes": [], "totalCount": 1},
        }]
        plan = {"repository": "berniemackie97/Praxis",
                "milestones": [{"stage": "M2", "title": "M2"}]}
        items = {19: {"kind": None, "stage": "M2", "area": "sync", "parent": 56}}

        with self.assertRaisesRegex(RuntimeError, "Refusing incomplete reconciliation"):
            reconcile_issues(plan, items, {19: {15}})
        execute.assert_not_called()

    def test_all_planned_dependencies_apply_without_mutating_completed_metadata(self):
        plan = RoadmapValidationTests.plan
        items, dependencies = validate(plan)
        titles = {m["stage"]: m["title"] for m in plan["milestones"]}
        existing = self._existing_issue_rows(items, titles)

        with patch("plan.gh_json", return_value=existing), patch("plan.gh") as execute:
            reconcile_issues(plan, items, dependencies)

        actual = {
            (call.args[2], call.args[6])
            for call in execute.call_args_list
        }
        expected = {
            (number, blocker)
            for number, blockers in dependencies.items()
            for blocker in blockers
        }
        self.assertEqual(expected, actual)
        self.assertEqual(len(expected), execute.call_count)
        for call in execute.call_args_list:
            self.assertEqual("issue", call.args[0])
            self.assertEqual("edit", call.args[1])
            self.assertEqual("--add-blocked-by", call.args[5])

    def test_reapplying_complete_roadmap_makes_no_changes(self):
        plan = RoadmapValidationTests.plan
        items, dependencies = validate(plan)
        titles = {m["stage"]: m["title"] for m in plan["milestones"]}
        existing = self._existing_issue_rows(items, titles, dependencies)

        with patch("plan.gh_json", return_value=existing), patch("plan.gh") as execute:
            reconcile_issues(plan, items, dependencies)

        execute.assert_not_called()

    @staticmethod
    def _existing_issue_rows(items, titles, dependencies=None):
        dependencies = dependencies or {}
        return [
            {
                "number": number,
                "title": f"[{(metadata['kind'] or 'task').title()}] Fixture",
                "labels": [
                    {"name": f"kind:{metadata['kind'] or 'task'}"},
                    {"name": f"area:{metadata['area']}"},
                    *([{"name": "needs:decision"}] if number == 50 else []),
                ],
                "milestone": (
                    {"title": titles[metadata['stage']]}
                    if metadata["stage"] else None
                ),
                "parent": (
                    {"number": metadata["parent"]}
                    if metadata["parent"] else None
                ),
                "blockedBy": {
                    "nodes": [{"number": blocker} for blocker in sorted(dependencies.get(number, ()))],
                    "totalCount": len(dependencies.get(number, ())),
                },
            }
            for number, metadata in items.items()
        ]

    @patch("plan.ensure_views")
    @patch("plan.gh")
    @patch("plan.gh_json")
    def test_project_existing_fields_and_status_are_preserved(self, fetch, execute, views):
        issue_url = "https://github.com/berniemackie97/Praxis/issues/19"

        def response(*args):
            if args[1] == "field-list":
                return {"fields": [
                    {"name": "Stage", "options": [{"name": f"M{i}"} for i in range(8)]},
                    {"name": "Priority", "options": [{"name": f"P{i}"} for i in range(4)]},
                    {"name": "Status", "options": [{"name": "Todo"}, {"name": "In progress"}, {"name": "Done"}]},
                ]}
            if args[1] == "item-list":
                return {"items": [{
                    "content": {"url": issue_url}, "Stage": "M2",
                    "Priority": "P0", "status": "In progress"
                }]}
            raise AssertionError(f"Unexpected GitHub command: {args}")

        fetch.side_effect = response
        plan = {"repository": "berniemackie97/Praxis",
                "project": {"owner": "berniemackie97", "number": 3}}
        project = {"id": "PVT_test", "description": "Existing project"}
        items = {19: {"stage": "M2", "priority": "P1"}}
        issues = {19: {"state": "OPEN"}}

        reconcile_project(plan, project, items, issues)

        execute.assert_not_called()
        views.assert_called_once_with("PVT_test")


if __name__ == "__main__":
    unittest.main()
