import copy
import json
import re
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

        self.assertEqual(62, len(items))
        self.assertEqual(57, items[92]["parent"])
        self.assertEqual("P1", items[92]["priority"])
        self.assertIn(92, dependencies[22])
        self.assertEqual(8, len(self.plan["milestones"]))
        self.assertEqual(14, len(self.plan["features"]))
        self.assertEqual(1, items[2]["parent"])
        self.assertEqual(3, items[54]["parent"])
        self.assertEqual(54, items[14]["parent"])
        self.assertEqual({15, 17, 40, 41}, dependencies[19])
        self.assertEqual({71, 73}, dependencies[67])

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
    @classmethod
    def setUpClass(cls):
        cls.plan = json.loads(
            Path(__file__).with_name("roadmap.json").read_text(encoding="utf-8")
        )

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
        plan = self.plan
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
        plan = self.plan
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


class ProjectReconciliationTests(unittest.TestCase):
    @staticmethod
    def fields():
        return {"totalCount": 3, "fields": [
            {"name": "Stage", "id": "PVTSSF_stage", "options": [
                {"name": f"M{i}", "id": f"stage{i}"} for i in range(8)]},
            {"name": "Priority", "id": "PVTSSF_priority", "options": [
                {"name": f"P{i}", "id": f"priority{i}"} for i in range(4)]},
            {"name": "Status", "id": "PVTSSF_status", "options": [
                {"name": name, "id": name.lower().replace(" ", "_")}
                for name in ("Todo", "In progress", "Done")]},
        ]}

    @staticmethod
    def plan():
        return {"repository": "berniemackie97/Praxis",
                "project": {"owner": "berniemackie97", "number": 3}}

    def test_existing_custom_values_are_not_overwritten(self):
        url = "https://github.com/berniemackie97/Praxis/issues/19"

        def fetch(*args):
            if args[1] == "field-list":
                return self.fields()
            self.assertEqual(("project", "item-list"), args[:2])
            self.assertNotIn("--field", args)
            return {"totalCount": 1, "items": [{
                "id": "PVTI_existing", "content": {"url": url},
                "stage": "M2", "priority": "P0", "status": "In progress",
            }]}

        with (patch("plan.gh_json", side_effect=fetch),
              patch("plan.graphql_mutations") as mutations,
              patch("plan.check_graphql_capacity"),
              patch("plan.ensure_views") as views,
              patch("plan.gh") as shell):
            reconcile_project(self.plan(), {
                "id": "PVT_project", "shortDescription": "Owner description",
            }, {19: {"stage": "M2", "priority": "P1"}}, {19: {"state": "OPEN"}})
            mutations.assert_not_called()
            shell.assert_not_called()
            views.assert_called_once_with("PVT_project")

    def test_missing_item_is_added_once_then_field_values_are_batched(self):
        url = "https://github.com/berniemackie97/Praxis/issues/73"

        def fetch(*args):
            if args[1] == "field-list":
                return self.fields()
            self.assertEqual(("project", "item-list"), args[:2])
            return {"totalCount": 0, "items": []}

        def batches(operations):
            if operations[0][0] == "add0":
                self.assertEqual(1, len(operations))
                self.assertIn("contentId: \"I_content\"", operations[0][1])
                return {"add0": {"item": {"id": "PVTI_new"}}}
            self.assertEqual(3, len(operations))
            for _, expression in operations:
                self.assertIn("itemId: \"PVTI_new\"", expression)
            return {alias: {"projectV2Item": {"id": "PVTI_new"}}
                    for alias, _ in operations}

        with (patch("plan.gh_json", side_effect=fetch),
              patch("plan.graphql_mutations", side_effect=batches) as mutations,
              patch("plan.check_graphql_capacity") as quota,
              patch("plan.ensure_views"),
              patch("plan.gh") as shell):
            reconcile_project(self.plan(), {
                "id": "PVT_project", "shortDescription": "Owner description",
            }, {73: {"stage": "M0", "priority": "P1"}},
                {73: {"state": "OPEN", "id": "I_content"}})
            self.assertEqual(2, mutations.call_count)
            quota.assert_called_once()
            shell.assert_not_called()

    def test_closed_item_updates_only_status(self):
        url = "https://github.com/berniemackie97/Praxis/issues/14"

        def fetch(*args):
            if args[1] == "field-list":
                return self.fields()
            return {"totalCount": 1, "items": [{
                "id": "PVTI_existing", "content": {"url": url},
                "stage": "M1", "priority": "P0", "status": "Todo",
            }]}

        with (patch("plan.gh_json", side_effect=fetch),
              patch("plan.graphql_mutations", return_value={
                  "set0": {"projectV2Item": {"id": "PVTI_existing"}}
              }) as mutations,
              patch("plan.check_graphql_capacity"),
              patch("plan.ensure_views"),
              patch("plan.gh") as shell):
            reconcile_project(self.plan(), {
                "id": "PVT_project", "shortDescription": "Owner description",
            }, {14: {"stage": "M1", "priority": "P1"}},
                {14: {"state": "CLOSED"}})
            updates = mutations.call_args.args[0]
            self.assertEqual(1, len(updates))
            self.assertIn('fieldId: "PVTSSF_status"', updates[0][1])
            self.assertIn('singleSelectOptionId: "done"', updates[0][1])
            shell.assert_not_called()

    def test_project_list_truncation_prevents_writes(self):
        def fetch(*args):
            if args[1] == "field-list":
                return self.fields()
            return {"totalCount": 2, "items": []}

        with (patch("plan.gh_json", side_effect=fetch),
              patch("plan.graphql_mutations") as mutations,
              patch("plan.check_graphql_capacity") as quota,
              patch("plan.ensure_views") as views,
              patch("plan.gh") as shell):
            with self.assertRaisesRegex(RuntimeError, "truncated"):
                reconcile_project(self.plan(), {"id": "PVT_project"}, {}, {})
            mutations.assert_not_called()
            quota.assert_not_called()
            views.assert_not_called()
            shell.assert_not_called()

    def test_missing_option_prevents_writes(self):
        invalid = self.fields()
        invalid["fields"][0]["options"].pop()

        with (patch("plan.gh_json", return_value=invalid),
              patch("plan.graphql_mutations") as mutations,
              patch("plan.ensure_views") as views):
            with self.assertRaisesRegex(RuntimeError, "missing required options"):
                reconcile_project(self.plan(), {"id": "PVT_project"}, {}, {})
            mutations.assert_not_called()
            views.assert_not_called()

    def test_full_roadmap_project_setup_and_complete_rerun(self):
        plan = json.loads(
            Path(__file__).with_name("roadmap.json").read_text(encoding="utf-8")
        )
        items, _ = validate(plan)
        state = {}
        all_issues = {
            number: {"state": "CLOSED" if number in (10, 11, 12, 14) else "OPEN",
                     "id": f"I_{number}"}
            for number in items
        }
        created = []
        updated = []

        def fetch(*args):
            if args[1] == "field-list":
                return self.fields()
            if args[1] == "item-list":
                return {"totalCount": len(state), "items": list(state.values())}
            raise AssertionError(f"Unexpected command: {args}")

        def apply_operations(operations):
            result = {}
            for alias, expression in operations:
                if alias.startswith("add"):
                    content_id = re.search(r'contentId: "([^"]+)"', expression).group(1)
                    number = int(content_id.removeprefix("I_"))
                    url = f"https://github.com/berniemackie97/Praxis/issues/{number}"
                    item_id = f"PVTI_{number}"
                    state[url] = {"id": item_id, "content": {"url": url}}
                    created.append(number)
                    result[alias] = {"item": {"id": item_id}}
                else:
                    item_id = re.search(r'itemId: "([^"]+)"', expression).group(1)
                    field_id = re.search(r'fieldId: "([^"]+)"', expression).group(1)
                    option_id = re.search(
                        r'singleSelectOptionId: "([^"]+)"', expression
                    ).group(1)
                    number = int(item_id.removeprefix("PVTI_"))
                    url = f"https://github.com/berniemackie97/Praxis/issues/{number}"
                    field = field_id.removeprefix("PVTSSF_")
                    values = {
                        "stage": option_id.removeprefix("stage"),
                        "priority": option_id.removeprefix("priority"),
                    }
                    state[url][field] = (
                        f"M{values['stage']}" if field == "stage" else
                        f"P{values['priority']}" if field == "priority" else
                        option_id.title()
                    )
                    updated.append((number, field))
                    result[alias] = {"projectV2Item": {"id": item_id}}
            return result

        with (patch("plan.gh_json", side_effect=fetch),
              patch("plan.graphql_mutations", side_effect=apply_operations) as graph,
              patch("plan.check_graphql_capacity"),
              patch("plan.ensure_views") as views,
              patch("plan.time.sleep"),
              patch("plan.gh") as shell):
            project = {"id": "PVT_project", "shortDescription": "Owner description"}
            reconcile_project(plan, project, items, all_issues)
            self.assertEqual(set(items), set(created))
            self.assertEqual(2 * (len(items) - 1) + len(items), len(updated))
            self.assertEqual(2, graph.call_count)
            graph.reset_mock()
            reconcile_project(plan, project, items, all_issues)
            graph.assert_not_called()
            shell.assert_not_called()
            self.assertEqual(2, views.call_count)

    def test_mutations_are_grouped_into_eight_operations_per_request(self):
        operations = [(f"m{i}", f'updateProjectV2ItemFieldValue(input: {{projectId: "P"}}) {{ projectV2Item {{ id }} }}')
                      for i in range(17)]
        responses = [
            {"data": {f"m{i}": {"projectV2Item": {"id": "P"}}
                      for i in range(start, min(start + 8, 17))}}
            for start in (0, 8, 16)
        ]

        with (patch("plan.gh_json", side_effect=responses) as fetch,
              patch("plan.time.sleep") as sleep):
            from plan import graphql_mutations
            self.assertEqual(17, len(graphql_mutations(operations)))
            self.assertEqual(3, fetch.call_count)
            self.assertEqual(2, sleep.call_count)
            for call in fetch.call_args_list:
                self.assertEqual(("api", "graphql", "-f"), call.args[:3])
                self.assertLessEqual(call.args[-1].count("updateProjectV2ItemFieldValue"), 8)

    def test_mutation_errors_abort_without_retries(self):
        from plan import graphql_mutations
        with (patch("plan.gh_json", return_value={
                "data": {"m0": {"projectV2Item": {"id": "P"}}},
                "errors": [{"message": "API rate limit exceeded"}]
              }) as fetch,
              patch("plan.time.sleep") as sleep):
            with self.assertRaisesRegex(RuntimeError, "Rerun to reconcile"):
                graphql_mutations([("m0", 'updateProjectV2ItemFieldValue(input: {}) { projectV2Item { id } }')])
            fetch.assert_called_once()
            sleep.assert_not_called()

    def test_rate_budget_stops_before_writes_with_reset_time(self):
        from plan import check_graphql_capacity
        with patch("plan.gh_json", return_value={
            "resources": {"graphql": {"remaining": 0, "reset": 1794000000}}
        }) as fetch:
            with self.assertRaisesRegex(RuntimeError, "Rate limit resets at"):
                check_graphql_capacity(20)
            fetch.assert_called_once_with("api", "rate_limit")

    def test_existing_empty_view_filter_is_completed_on_rerun(self):
        from plan import ensure_views
        views = [("Delivery Board", "BOARD_LAYOUT", "is:issue is:open")]
        response = {"data": {"node": {"views": {"totalCount": 1, "nodes": [
            {"id": "PVT_view", "name": "Delivery Board", "filter": None}
        ]}}}}
        update = {"data": {"updateProjectV2View": {"projectV2View": {"id": "PVT_view"}}}}
        with (patch("plan.VIEWS", views),
              patch("plan.gh_json", side_effect=[response, update]) as fetch,
              patch("plan.check_graphql_capacity") as quota,
              patch("plan.time.sleep") as sleep):
            ensure_views("PVT_project")
            self.assertEqual(2, fetch.call_count)
            quota.assert_called_once_with(10)
            sleep.assert_called_once()

    def test_existing_custom_view_filter_is_retained(self):
        from plan import ensure_views
        views = [("Delivery Board", "BOARD_LAYOUT", "is:issue is:open")]
        response = {"data": {"node": {"views": {"totalCount": 1, "nodes": [
            {"id": "PVT_view", "name": "Delivery Board", "filter": "status:In Progress"}
        ]}}}}
        with (patch("plan.VIEWS", views),
              patch("plan.gh_json", return_value=response) as fetch,
              patch("plan.check_graphql_capacity") as quota):
            ensure_views("PVT_project")
            fetch.assert_called_once()
            quota.assert_not_called()



if __name__ == "__main__":
    unittest.main()
