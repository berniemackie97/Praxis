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

        self.assertEqual(253, len(items))
        for number in (514, 515):
            self.assertEqual(487, items[number]["parent"])
            self.assertEqual(("M5", "P1", "intelligence"),
                             (items[number]["stage"], items[number]["priority"], items[number]["area"]))
            self.assertIn(number, self.plan["delivery_lanes"][7]["issues"])
            self.assertNotIn(number, dependencies[170])
        self.assertEqual({488, 489, 339, 340}, dependencies[514])
        self.assertEqual({173, 488, 463}, dependencies[515])
        for number, parent, area in ((510, 97, "intelligence"), (511, 293, "knowledge"), (512, 293, "clients")):
            self.assertEqual(parent, items[number]["parent"])
            self.assertEqual(("M5", "P1", area),
                             (items[number]["stage"], items[number]["priority"], items[number]["area"]))
        self.assertEqual({115, 116}, dependencies[510])
        self.assertEqual({102}, dependencies[511])
        self.assertEqual({511}, dependencies[512])
        self.assertIn(510, self.plan["delivery_lanes"][2]["issues"])
        self.assertIn(511, self.plan["delivery_lanes"][6]["issues"])
        self.assertIn(512, self.plan["delivery_lanes"][6]["issues"])
        for number in (510, 511, 512):
            self.assertNotIn(number, dependencies[170])
        self.assertEqual(293, items[507]["parent"])
        self.assertEqual(("M5", "P1", "knowledge"),
                         (items[507]["stage"], items[507]["priority"], items[507]["area"]))
        self.assertEqual({306, 115, 116, 507}, dependencies[314])
        self.assertEqual({314, 507}, dependencies[315])
        self.assertNotIn(315, dependencies[314])
        self.assertIn(507, self.plan["delivery_lanes"][6]["issues"])
        self.assertNotIn(507, dependencies[170])
        self.assertEqual(300, items[440]["parent"])
        self.assertEqual(97, items[452]["parent"])
        # Post-MVP experience-based Brain, governed model training and agent teams.
        self.assertEqual(59, items[485]["parent"])
        self.assertEqual({101, 102, 103}, dependencies[485])
        self.assertEqual(7, items[487]["parent"])
        self.assertEqual(487, items[488]["parent"])
        self.assertEqual(487, items[489]["parent"])
        self.assertEqual(287, items[490]["parent"])
        for number in (491, 492, 493):
            self.assertEqual(490, items[number]["parent"])
        self.assertEqual(289, items[494]["parent"])
        self.assertEqual({488, 489, 311}, dependencies[491])
        self.assertEqual({491, 311, 313, 303, 307}, dependencies[492])
        self.assertEqual({173, 291, 334}, dependencies[494])
        self.assertEqual("P3", items[493]["priority"])
        self.assertEqual("P3", items[494]["priority"])
        self.assertTrue(set(range(487, 495)).issubset(set(self.plan["delivery_lanes"][7]["issues"])))
        self.assertTrue(set(range(487, 495)).isdisjoint(dependencies[170]))

        self.assertEqual(61, items[451]["parent"])
        self.assertEqual("retrieval", items[451]["area"])
        self.assertEqual(436, items[433]["parent"])
        self.assertEqual(433, items[437]["parent"])
        self.assertEqual(383, items[455]["parent"])
        self.assertEqual(383, items[456]["parent"])
        self.assertEqual(97, items[463]["parent"])
        self.assertEqual(("M3", "P1", "knowledge"),
                         (items[455]["stage"], items[455]["priority"], items[455]["area"]))
        self.assertEqual(("M3", "P1", "clients"),
                         (items[456]["stage"], items[456]["priority"], items[456]["area"]))
        self.assertEqual(("M5", "P1", "intelligence"),
                         (items[463]["stage"], items[463]["priority"], items[463]["area"]))
        self.assertEqual({455}, dependencies[456])
        self.assertEqual({387}, dependencies[455])
        self.assertIn(455, self.plan["delivery_lanes"][6]["issues"])
        self.assertIn(463, self.plan["delivery_lanes"][2]["issues"])
        self.assertEqual({431, 432, 143}, dependencies[440])
        self.assertEqual({106, 316, 311}, dependencies[451])
        self.assertEqual({324, 316, 353}, dependencies[452])
        self.assertEqual("M6", items[443]["stage"])
        self.assertEqual("P0", items[431]["priority"])
        self.assertEqual(59, items[136]["parent"])
        self.assertEqual("P2", items[136]["priority"])
        self.assertEqual("knowledge", items[136]["area"])
        self.assertEqual(59, items[100]["parent"])
        self.assertEqual(59, items[122]["parent"])
        self.assertEqual(59, items[124]["parent"])
        self.assertEqual("knowledge", items[124]["area"])
        self.assertEqual("P1", items[124]["priority"])
        self.assertEqual("P1", items[122]["priority"])
        self.assertIn(122, dependencies[100])
        self.assertIn(124, dependencies[100])
        self.assertIn(126, dependencies[100])
        self.assertIn(128, dependencies[100])
        self.assertEqual({124}, dependencies[126])
        self.assertEqual({126}, dependencies[128])
        self.assertEqual({99, 100, 129}, dependencies[107])
        self.assertEqual(set(), dependencies[36])
        self.assertNotIn("36", self.plan["dependency_removals"])
        self.assertEqual({100, 101, 102, 103, 105, 106, 108, 113, 129, 146, 166, 215}, dependencies[130])
        self.assertEqual({108}, dependencies[215])
        self.assertEqual(64, items[215]["parent"])
        self.assertEqual("P1", items[215]["priority"])
        self.assertEqual(2, items[219]["parent"])
        self.assertEqual("P2", items[219]["priority"])
        self.assertEqual(59, items[126]["parent"])
        self.assertEqual(59, items[128]["parent"])
        self.assertEqual(64, items[129]["parent"])
        self.assertEqual(64, items[130]["parent"])
        self.assertEqual("security", items[129]["area"])
        self.assertEqual({122}, dependencies[124])
        self.assertEqual({99}, dependencies[122])
        self.assertEqual("knowledge", items[100]["area"])
        self.assertEqual(96, items[109]["parent"])
        self.assertEqual(97, items[115]["parent"])
        self.assertEqual(98, items[117]["parent"])
        self.assertEqual("security", items[117]["area"])
        self.assertEqual(64, items[108]["parent"])
        self.assertEqual(64, items[146]["parent"])
        self.assertEqual(64, items[166]["parent"])
        self.assertEqual("P1", items[166]["priority"])
        self.assertEqual({126, 108}, dependencies[166])
        self.assertEqual("P1", items[146]["priority"])
        self.assertEqual({106}, dependencies[108])
        self.assertEqual({106}, dependencies[146])
        self.assertEqual([101, 107], self.plan["dependency_removals"]["108"])
        self.assertEqual([107], self.plan["dependency_removals"]["130"])
        self.assertEqual("P1", items[108]["priority"])
        self.assertEqual({101, 109, 111}, dependencies[26])
        self.assertNotIn(22, dependencies[26])
        self.assertEqual({14, 16, 102}, dependencies[27])
        self.assertEqual([99, 122, 124, 126, 128, 100, 102, 101, 105, 103, 106, 129, 108, 146, 113, 166, 215, 41, 130],
                         self.plan["delivery_lanes"][0]["issues"])
        self.assertEqual("P1", items[105]["priority"])
        self.assertEqual({"from": "P2", "to": "P1"},
                         self.plan["priority_migrations"]["105"])
        self.assertNotIn(105, self.plan["delivery_lanes"][2]["issues"])
        self.assertIn(107, self.plan["delivery_lanes"][3]["issues"])
        self.assertNotIn(113, self.plan["delivery_lanes"][1]["issues"])
        self.assertEqual({101, 106}, dependencies[113])
        self.assertEqual([108], self.plan["dependency_removals"]["113"])
        self.assertEqual(57, items[94]["parent"])
        self.assertEqual("P1", items[94]["priority"])
        self.assertEqual(57, items[92]["parent"])
        self.assertEqual("P1", items[92]["priority"])
        self.assertEqual({106, 108}, dependencies[385])
        self.assertEqual(8, len(self.plan["milestones"]))
        self.assertEqual(40, len(self.plan["features"]))
        for epic in (287, 288, 289, 290):
            self.assertEqual("epic", items[epic]["kind"])
            self.assertEqual(1, items[epic]["parent"])
        self.assertEqual(287, items[291]["parent"])
        self.assertEqual(291, items[303]["parent"])
        self.assertEqual({31}, dependencies[303])
        self.assertEqual({303, 172}, dependencies[304])
        self.assertEqual("security", items[318]["area"])
        self.assertIn(308, self.plan["decision_queue"])
        self.assertEqual(1, items[2]["parent"])
        self.assertEqual(3, items[54]["parent"])
        self.assertEqual(54, items[14]["parent"])
        self.assertEqual({15, 17, 40, 41}, dependencies[19])
        self.assertEqual(56, items[247]["parent"])
        self.assertEqual("sync", items[247]["area"])
        self.assertEqual("P2", items[247]["priority"])
        self.assertEqual({15, 17}, dependencies[247])
        self.assertEqual({15, 17, 19, 247}, dependencies[18])
        self.assertEqual(7, items[176]["parent"])
        for number in (177, 185, 179, 181, 184):
            self.assertEqual(176, items[number]["parent"])
        self.assertEqual("retrieval", items[181]["area"])
        self.assertEqual("clients", items[177]["area"])
        self.assertEqual("knowledge", items[184]["area"])
        for number in (176, 177, 185, 179, 181, 184, 114):
            self.assertEqual("P1", items[number]["priority"])
        self.assertEqual({103, 106, 114}, dependencies[181])
        self.assertEqual({181, 114, 103, 106, 29, 31, 33, 32, 166}, dependencies[177])
        self.assertEqual({102, 103, 129}, dependencies[184])
        self.assertEqual({184, 103, 115, 31, 32, 33, 173}, dependencies[185])
        self.assertEqual({177, 185, 156, 29}, dependencies[179])
        self.assertIn(176, dependencies[170])
        self.assertIn(179, dependencies[170])
        self.assertIn(181, self.plan["delivery_lanes"][4]["issues"])
        self.assertIn(184, self.plan["delivery_lanes"][4]["issues"])
        self.assertIn(177, self.plan["delivery_lanes"][6]["issues"])
        self.assertIn(185, self.plan["delivery_lanes"][6]["issues"])
        self.assertIn(179, self.plan["delivery_lanes"][6]["issues"])
        self.assertEqual({"from": "P2", "to": "P1"},
                         self.plan["priority_migrations"]["114"])
        # Native graph exploration replaces mandatory third-party editor projections.
        self.assertEqual("M3 — Native knowledge exploration and portability",
                         next(m["title"] for m in self.plan["milestones"] if m["stage"] == "M3"))
        self.assertEqual("P1", items[5]["priority"])
        self.assertEqual(5, items[383]["parent"])
        self.assertEqual("clients", items[383]["area"])
        self.assertEqual("P1", items[383]["priority"])
        self.assertEqual({108}, dependencies[384])
        self.assertEqual({384, 385}, dependencies[386])
        self.assertEqual({385, 386}, dependencies[387])
        self.assertEqual({385, 386, 387}, dependencies[388])
        self.assertIn(388, dependencies[170])
        self.assertNotIn(171, dependencies[170])
        self.assertEqual([171], self.plan["dependency_removals"]["170"])
        self.assertEqual([143, 149], self.plan["dependency_removals"]["171"])
        self.assertEqual(57, items[171]["parent"])
        self.assertEqual("P2", items[57]["priority"])
        self.assertEqual("P2", items[171]["priority"])
        self.assertEqual(8, items[58]["parent"])
        self.assertEqual("M6", items[58]["stage"])
        self.assertEqual(58, items[24]["parent"])
        self.assertEqual({18, 39}, dependencies[24])
        self.assertNotIn(22, items)
        self.assertNotIn(23, items)
        self.assertNotIn(178, items)
        self.assertEqual(176, items[185]["parent"])
        self.assertEqual(2, items[382]["parent"])
        self.assertEqual(61, items[379]["parent"])
        self.assertEqual(64, items[380]["parent"])
        self.assertEqual(292, items[381]["parent"])
        self.assertIn(383, self.plan["delivery_lanes"][6]["issues"])
        self.assertIn(388, self.plan["delivery_lanes"][6]["issues"])
        self.assertNotIn(57, self.plan["delivery_lanes"][6]["issues"])
        self.assertNotIn(171, self.plan["delivery_lanes"][6]["issues"])
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

    def test_invalid_retired_edge_is_rejected(self):
        data = copy.deepcopy(self.plan)
        data["dependency_removals"]["26"] = [101]
        with self.assertRaisesRegex(ValueError, "Invalid retired dependency"):
            validate(data)

    def test_unknown_delivery_lane_item_is_rejected(self):
        data = copy.deepcopy(self.plan)
        data["delivery_lanes"][0]["issues"].append(9999)
        with self.assertRaisesRegex(ValueError, "Invalid or duplicate lane issue"):
            validate(data)

    def test_duplicate_delivery_lane_item_is_rejected(self):
        data = copy.deepcopy(self.plan)
        data["delivery_lanes"][1]["issues"].append(99)
        with self.assertRaisesRegex(ValueError, "Invalid or duplicate lane issue"):
            validate(data)

    def test_priority_migration_requires_expected_new_priority(self):
        data = copy.deepcopy(self.plan)
        data["priority_migrations"]["59"]["to"] = "P0"
        with self.assertRaisesRegex(ValueError, "Invalid priority migration"):
            validate(data)

    def test_area_override_rejects_invalid_label(self):
        data = copy.deepcopy(self.plan)
        data["area_overrides"]["100"] = "unknown"
        with self.assertRaisesRegex(ValueError, "Invalid area override"):
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
    def test_declared_retired_dependency_is_removed_without_touching_others(self, fetch, execute):
        fetch.return_value = [{
            "number": 26, "title": "[Story] Capture",
            "labels": [{"name": "kind:story"}, {"name": "area:capture"}],
            "milestone": {"title": "M4"}, "parent": {"number": 59},
            "blockedBy": {"nodes": [{"number": 22}, {"number": 101}, {"number": 999}],
                          "totalCount": 3},
        }]
        plan = {"repository": "berniemackie97/Praxis",
                "milestones": [{"stage": "M4", "title": "M4"}],
                "dependency_removals": {"26": [22]}}
        items = {26: {"kind": None, "stage": "M4", "area": "capture", "parent": 59}}
        reconcile_issues(plan, items, {26: {101}})
        execute.assert_called_once_with(
            "issue", "edit", 26, "-R", "berniemackie97/Praxis",
            "--remove-blocked-by", 22
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

    def _existing_issue_rows(self, items, titles, dependencies=None):
        dependencies = dependencies or {}
        return [
            {
                "number": number,
                "title": f"[{(metadata['kind'] or 'task').title()}] Fixture",
                "labels": [
                    {"name": f"kind:{metadata['kind'] or 'task'}"},
                    {"name": f"area:{metadata['area']}"},
                    *([{"name": "needs:decision"}]
                      if number in self.plan.get("decision_queue", [50]) else []),
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

    def test_scoped_priority_migration_updates_only_expected_old_value(self):
        url = "https://github.com/berniemackie97/Praxis/issues/59"
        def fetch(*args):
            if args[1] == "field-list":
                return self.fields()
            return {"totalCount": 1, "items": [{
                "id": "PVTI_existing", "content": {"url": url},
                "stage": "M4", "priority": "P2", "status": "Todo"
            }]}
        plan = {**self.plan(),
                "priority_migrations": {"59": {"from": "P2", "to": "P1"}}}
        with (patch("plan.gh_json", side_effect=fetch),
              patch("plan.graphql_mutations", return_value={
                  "set0": {"projectV2Item": {"id": "PVTI_existing"}}
              }) as mutations,
              patch("plan.check_graphql_capacity"),
              patch("plan.ensure_views"),
              patch("plan.gh") as shell):
            reconcile_project(plan, {"id": "PVT_project",
                "shortDescription": "Curated"}, {59: {"stage": "M4", "priority": "P1"}},
                {59: {"state": "OPEN"}})
            updates = mutations.call_args.args[0]
            self.assertEqual(1, len(updates))
            self.assertIn('singleSelectOptionId: "priority1"', updates[0][1])
            shell.assert_not_called()

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
