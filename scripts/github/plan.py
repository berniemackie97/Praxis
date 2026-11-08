#!/usr/bin/env python3
"""Validate or reconcile the repository's GitHub delivery metadata."""

import argparse
import json
import re
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path

PLAN_PATH = Path(__file__).with_name("roadmap.json")
VIEWS = (
    ("Delivery Board", "BOARD_LAYOUT", "is:issue is:open"),
    ("Feature Portfolio", "TABLE_LAYOUT", 'label:"kind:feature"'),
    ("Decision Queue", "TABLE_LAYOUT", 'label:"needs:decision"'),
    ("Defect Triage", "TABLE_LAYOUT", 'label:"kind:bug"'),
    ("Milestone Portfolio", "TABLE_LAYOUT", "is:issue"),
)

KIND_COLORS = {
    "roadmap": "57606A",
    "epic": "8250DF",
    "feature": "0969DA",
    "story": "1A7F37",
    "task": "BF8700",
    "spike": "DB6D28",
    "bug": "CF222E",
}
AREAS = {
    "delivery": "57606A",
    "security": "CF222E",
    "knowledge": "1A7F37",
    "sync": "0969DA",
    "obsidian": "8250DF",
    "capture": "0A7E8C",
    "retrieval": "1A7F37",
    "intelligence": "8250DF",
    "automation": "BF8700",
    "clients": "0969DA",
    "operations": "57606A",
}


def gh(*args):
    completed = subprocess.run(
        ("gh", *map(str, args)),
        check=False,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if completed.returncode:
        raise RuntimeError(f"gh {' '.join(map(str, args[:3]))}: {completed.stderr.strip()}")
    return completed.stdout


def gh_json(*args):
    return json.loads(gh(*args))


def validate(plan):
    stages = {m["stage"] for m in plan["milestones"]}
    if stages != {f"M{index}" for index in range(8)}:
        raise ValueError("Exactly eight distinct milestone stages M0-M7 are required.")
    if len({m["title"] for m in plan["milestones"]}) != len(stages):
        raise ValueError("Milestone titles must be distinct.")

    items = {1: {"kind": "roadmap", "parent": None, "stage": None, "area": "delivery"}}

    def add(number, kind, parent, stage, area):
        if number in items:
            raise ValueError(f"Duplicate work item #{number}.")
        if stage not in stages or area not in AREAS:
            raise ValueError(f"Invalid stage or area on #{number}.")
        items[number] = {"kind": kind, "parent": parent, "stage": stage, "area": area}

    epics = {}
    for epic in plan["epics"]:
        number = epic["issue"]
        if number in epics:
            raise ValueError(f"Duplicate epic #{number}.")
        epics[number] = epic
        add(number, "epic", 1, epic["stage"], epic["area"])

    for feature in plan["features"]:
        number, epic_number = feature["issue"], feature["epic"]
        if epic_number not in epics:
            raise ValueError(f"Unknown epic #{epic_number} for feature #{number}.")
        add(number, "feature", epic_number, epics[epic_number]["stage"], feature["area"])

    for feature in plan["features"]:
        for number in feature["children"]:
            add(number, None, feature["issue"], items[feature["issue"]]["stage"], feature["area"])

    for task in plan["direct_work"]:
        epic_number = task["epic"]
        if epic_number not in epics:
            raise ValueError(f"Unknown epic #{epic_number}.")
        add(task["issue"], None, epic_number, epics[epic_number]["stage"], epics[epic_number]["area"])

    for number, area in plan.get("area_overrides", {}).items():
        if int(number) not in items or area not in AREAS:
            raise ValueError(f"Invalid area override on #{number}.")
        items[int(number)]["area"] = area
    if not set(plan.get("decision_queue", [])).issubset(items):
        raise ValueError("Decision queue contains an unknown issue.")

    dependencies = {int(issue): set(blockers) for issue, blockers in plan["dependencies"].items()}
    for number, blockers in dependencies.items():
        if number not in items or number in blockers or not blockers.issubset(items):
            raise ValueError(f"Invalid dependency edge on #{number}.")

    removed = {int(number): set(blockers)
               for number, blockers in plan.get("dependency_removals", {}).items()}
    for number, blockers in removed.items():
        if (number not in items or not blockers or not blockers.issubset(items) or
                number in blockers or blockers.intersection(dependencies.get(number, set()))):
            raise ValueError(f"Invalid retired dependency on #{number}.")

    lane_ids = set()
    lane_items = set()
    for lane in plan.get("delivery_lanes", []):
        if lane["id"] in lane_ids or not lane["issues"] or not lane["exit"]:
            raise ValueError("Delivery lanes need distinct identities and exit evidence.")
        lane_ids.add(lane["id"])
        for number in lane["issues"]:
            if number not in items or number in lane_items:
                raise ValueError(f"Invalid or duplicate lane issue #{number}.")
            lane_items.add(number)

    visited = set()
    active = set()

    def visit(number):
        if number in active:
            raise ValueError(f"Cyclic issue dependencies including #{number}.")
        if number in visited:
            return
        active.add(number)
        for blocker in dependencies.get(number, ()):
            visit(blocker)
        active.remove(number)
        visited.add(number)

    for number in items:
        visit(number)

    if set(plan["priorities"]) != stages:
        raise ValueError("Priority defaults must cover every stage.")
    if not set(plan["priorities"].values()).issubset({"P0", "P1", "P2", "P3"}):
        raise ValueError("Invalid default priority.")
    for number, priority in plan["priority_overrides"].items():
        if int(number) not in items or priority not in {"P0", "P1", "P2", "P3"}:
            raise ValueError(f"Invalid priority override on #{number}.")

    for number, item in items.items():
        stage = item["stage"]
        item["priority"] = plan["priority_overrides"].get(
            str(number), plan["priorities"].get(stage)
        )
    for number, migration in plan.get("priority_migrations", {}).items():
        if (int(number) not in items or
                set(migration) != {"from", "to"} or
                migration["from"] not in {"P0", "P1", "P2", "P3"} or
                migration["from"] == migration["to"] or
                migration["to"] != items[int(number)]["priority"]):
            raise ValueError(f"Invalid priority migration on #{number}.")
    return items, dependencies


def verify_target(plan):
    version = gh("--version").splitlines()[0]
    match = re.search(r"\b(\d+)\.(\d+)\.(\d+)\b", version)
    if not match or tuple(map(int, match.groups())) < (2, 94, 0):
        raise RuntimeError("GitHub CLI 2.94 or later is required: brew upgrade gh")

    login = gh("api", "user", "--jq", ".login").strip()
    owner = plan["project"]["owner"]
    if login != owner:
        raise RuntimeError(f"Authenticated GitHub account is {login}, not {owner}.")
    repo = gh_json("api", f"repos/{plan['repository']}")
    if repo["full_name"] != plan["repository"] or not repo["permissions"]["admin"]:
        raise RuntimeError("Admin permission for the intended repository is required.")
    project = gh_json(
        "project", "view", plan["project"]["number"], "--owner", owner, "--format", "json"
    )
    if project["title"] != "Praxis Roadmap":
        raise RuntimeError("Project number did not resolve to the expected board.")
    return project


def ensure_milestones(plan):
    repo = plan["repository"]
    existing = {
        entry["title"]: entry
        for entry in gh_json("api", "-X", "GET", f"repos/{repo}/milestones",
                             "-f", "state=all", "-f", "per_page=100")
    }
    for milestone in plan["milestones"]:
        title = milestone["title"]
        if title in existing:
            continue
        gh("api", "-X", "POST", f"repos/{repo}/milestones",
           "-f", f"title={title}", "-f", f"description={milestone['description']}")
        print(f"Created milestone: {title}")


def ensure_labels(plan):
    repo = plan["repository"]
    existing = {
        label["name"] for label in gh_json(
            "label", "list", "-R", repo, "--limit", "200", "--json", "name"
        )
    }
    labels = {
        **{f"kind:{name}": (color, f"{name.title()} work item")
           for name, color in KIND_COLORS.items()},
        **{f"area:{name}": (color, f"{name.title()} product area")
           for name, color in AREAS.items()},
        "needs:decision": ("D4A72C", "Requires an explicit architecture or product decision"),
    }
    for label, (color, description) in labels.items():
        if label in existing:
            continue
        gh("label", "create", label, "-R", repo,
           "--color", color, "--description", description)
        print(f"Created label: {label}")


def blocked_by_numbers(issue):
    relationship = issue["blockedBy"]
    if relationship is None:
        return set()

    nodes = relationship["nodes"]
    total = relationship["totalCount"]
    if len(nodes) != total:
        raise RuntimeError(
            f"Issue #{issue['number']} has {total} blocking dependencies, "
            f"but GitHub CLI returned only {len(nodes)}. Refusing incomplete reconciliation."
        )
    return {node["number"] for node in nodes}


def reconcile_issues(plan, items, dependencies):
    repo = plan["repository"]
    issues = {
        issue["number"]: issue
        for issue in gh_json(
            "issue", "list", "-R", repo, "--state", "all", "--limit", "200",
            "--json", "number,title,state,labels,milestone,parent,blockedBy",
        )
    }
    missing = set(items) - set(issues)
    if missing:
        raise RuntimeError(f"Missing tracked issues: {sorted(missing)}")

    titles = {m["stage"]: m["title"] for m in plan["milestones"]}
    for number, metadata in sorted(items.items()):
        issue = issues[number]
        found = re.match(r"^\[(\w+)\]", issue["title"])
        kind = found.group(1).lower() if found else None
        expected = metadata["kind"]
        if kind not in KIND_COLORS or (expected is not None and kind != expected):
            raise RuntimeError(f"Unexpected issue classification on #{number}: {issue['title']}")

        desired = {f"kind:{kind}", f"area:{metadata['area']}"}
        if number in plan.get("decision_queue", [50]):
            desired.add("needs:decision")
        present = {label["name"] for label in issue.get("labels") or []}
        missing_labels = desired - present
        if missing_labels:
            gh("issue", "edit", number, "-R", repo,
               "--add-label", ",".join(sorted(missing_labels)))

        stage = metadata["stage"]
        previous = (issue.get("milestone") or {}).get("title")
        if stage is not None and previous != titles[stage]:
            if previous:
                raise RuntimeError(f"Milestone on #{number} is already {previous!r}.")
            gh("issue", "edit", number, "-R", repo, "--milestone", titles[stage])

        parent = metadata["parent"]
        assigned = (issue.get("parent") or {}).get("number")
        if parent is not None and assigned != parent:
            if assigned is not None:
                raise RuntimeError(f"Issue #{number} already has parent #{assigned}.")
            gh("issue", "edit", number, "-R", repo, "--parent", parent)

        blocked_by = blocked_by_numbers(issue)
        retired = set(plan.get("dependency_removals", {}).get(str(number), []))
        for dependency in sorted(retired & blocked_by):
            gh("issue", "edit", number, "-R", repo, "--remove-blocked-by", dependency)
        for dependency in sorted(dependencies.get(number, set()) - blocked_by):
            gh("issue", "edit", number, "-R", repo, "--add-blocked-by", dependency)

        print(f"Reconciled #{number}: {kind} / {stage or 'portfolio'}")


def check_graphql_capacity(minimum):
    quota = gh_json("api", "rate_limit")["resources"]["graphql"]
    if quota["remaining"] < minimum:
        reset = datetime.fromtimestamp(quota["reset"], timezone.utc).isoformat()
        raise RuntimeError(
            f"GraphQL quota is {quota['remaining']} points; {minimum} required "
            f"before further writes. Rate limit resets at {reset}. "
            "Existing changes are retained. Do not repeatedly retry while limited."
        )


def graphql_mutations(operations):
    results = {}
    for offset in range(0, len(operations), 8):
        if offset:
            time.sleep(1)
        chunk = operations[offset:offset + 8]
        query = "mutation {\n" + "\n".join(
            f"  {alias}: {expression}" for alias, expression in chunk
        ) + "\n}"
        response = gh_json("api", "graphql", "-f", f"query={query}")
        if response.get("errors"):
            raise RuntimeError(
                f"Project mutation batch {offset // 8 + 1} failed: "
                f"{response['errors']}. Rerun to reconcile successful earlier batches."
            )

        data = response["data"]
        for alias, _ in chunk:
            if data.get(alias) is None:
                raise RuntimeError(
                    f"Missing result for {alias}; reread the Project before retrying."
                )
            results[alias] = data[alias]
        print(f"Applied Project mutation batch {offset // 8 + 1}.")
    return results


def ensure_views(project_id):
    query = """query($projectId: ID!) {
      node(id: $projectId) {
        ... on ProjectV2 { views(first: 50) {
          totalCount nodes { id name filter }
        } }
      }
    }"""
    data = gh_json("api", "graphql", "-f", f"query={query}", "-f",
                   f"projectId={project_id}")
    views = data["data"]["node"]["views"]
    if len(views["nodes"]) != views["totalCount"]:
        raise RuntimeError("Project views are truncated; refusing reconciliation.")
    existing = {view["name"]: view for view in views["nodes"]}
    create = """mutation($projectId: ID!, $name: String!,
                        $layout: ProjectV2ViewLayout!) {
      createProjectV2View(input: {
        projectId: $projectId, name: $name, layout: $layout
      }) { projectV2View { id } }
    }"""
    update = """mutation($id: ID!, $filter: String!) {
      updateProjectV2View(input: { viewId: $id, filter: $filter }) {
        projectV2View { id }
      }
    }"""
    for name, layout, filter_query in VIEWS:
        view = existing.get(name)
        if view is None:
            check_graphql_capacity(10)
            created = gh_json(
                "api", "graphql", "-f", f"query={create}", "-f",
                f"projectId={project_id}", "-f", f"name={name}", "-f", f"layout={layout}"
            )
            view = {"id": created["data"]["createProjectV2View"]["projectV2View"]["id"],
                    "filter": None}
            print(f"Created view: {name}")
            time.sleep(1)

        if view["filter"] == filter_query:
            continue
        if view["filter"]:
            print(f"Preserved custom view filter: {name}")
            continue
        check_graphql_capacity(10)
        gh_json("api", "graphql", "-f", f"query={update}", "-f", f"id={view['id']}",
                "-f", f"filter={filter_query}")
        print(f"Configured view: {name}")
        time.sleep(1)


def reconcile_project(plan, project, items, issues):
    repo = plan["repository"]
    owner, number = plan["project"]["owner"], plan["project"]["number"]

    field_response = gh_json("project", "field-list", number, "--owner", owner,
                             "--format", "json", "--limit", "100")
    fields = field_response["fields"]
    if len(fields) != field_response["totalCount"]:
        raise RuntimeError("Project fields are truncated; refusing reconciliation.")
    by_name = {field["name"]: field for field in fields}
    if not {"Stage", "Priority", "Status"}.issubset(by_name):
        raise RuntimeError("Project must contain Stage, Priority and Status fields.")
    options = {
        name: {value["name"]: value["id"] for value in by_name[name]["options"]}
        for name in ("Stage", "Priority", "Status")
    }
    for field_name, required in (
        ("Stage", {f"M{i}" for i in range(8)}),
        ("Priority", {"P0", "P1", "P2", "P3"}),
    ):
        if not required.issubset(options[field_name]):
            raise RuntimeError(f"{field_name} field is missing required options.")

    listing = gh_json("project", "item-list", number, "--owner", owner,
                      "--format", "json", "--limit", "200")
    existing = listing["items"]
    if len(existing) != listing["totalCount"]:
        raise RuntimeError("Project item list is truncated; refusing reconciliation.")
    project_items = {
        entry["content"]["url"]: entry
        for entry in existing if isinstance(entry.get("content"), dict)
        and entry["content"].get("url")
    }
    backlog = next((value for value in ("Todo", "Backlog", "Ready")
                    if value in options["Status"]), None)

    missing = [
        issue_number for issue_number in sorted(items)
        if f"https://github.com/{repo}/issues/{issue_number}" not in project_items
    ]
    if missing:
        for issue_number in missing:
            if not issues[issue_number].get("id"):
                raise RuntimeError(f"Issue #{issue_number} is missing its GraphQL ID.")

    # One preflight for the complete planned batch workload, before creating items.
    maximum_changes = len(missing) + 3 * len(items)
    if maximum_changes:
        check_graphql_capacity(max(50, 5 * ((maximum_changes + 7) // 8) + 20))

    if missing:
        add_operations = [
            (f"add{index}",
             "addProjectV2ItemById(input: {"
             f"projectId: {json.dumps(project['id'])}, "
             f"contentId: {json.dumps(issues[issue_number]['id'])}"
             "}) { item { id } }")
            for index, issue_number in enumerate(missing)
        ]
        added = graphql_mutations(add_operations)
        for index, issue_number in enumerate(missing):
            url = f"https://github.com/{repo}/issues/{issue_number}"
            project_items[url] = {
                "id": added[f"add{index}"]["item"]["id"],
                "content": {"url": url}
            }

    changes = []
    for issue_number, metadata in sorted(items.items()):
        url = f"https://github.com/{repo}/issues/{issue_number}"
        entry = project_items[url]
        item_id = entry["id"]
        for field_name, value in (
            ("Stage", metadata["stage"]),
            ("Priority", metadata["priority"]),
        ):
            if value is None:
                continue
            current = entry.get(field_name.lower())
            if current == value:
                continue
            migration = plan.get("priority_migrations", {}).get(str(issue_number))
            if (field_name == "Priority" and migration and
                    current == migration["from"] and value == migration["to"]):
                changes.append((item_id, by_name[field_name]["id"], options[field_name][value]))
                continue
            if current:
                print(f"Preserved existing {field_name}={current} on #{issue_number}")
                continue
            changes.append((item_id, by_name[field_name]["id"], options[field_name][value]))

        current_status = entry.get("status")
        target_status = "Done" if issues[issue_number]["state"] == "CLOSED" else backlog
        if target_status and (
            not current_status or
            (issues[issue_number]["state"] == "CLOSED" and current_status != "Done")
        ):
            changes.append((item_id, by_name["Status"]["id"], options["Status"][target_status]))

    if missing and changes:
        time.sleep(1)

    if changes:
        operations = [
            (f"set{index}",
             "updateProjectV2ItemFieldValue(input: {"
             f"projectId: {json.dumps(project['id'])}, "
             f"itemId: {json.dumps(item_id)}, "
             f"fieldId: {json.dumps(field_id)}, "
             f"value: {{singleSelectOptionId: {json.dumps(option_id)}}}"
             "}) { projectV2Item { id } }")
            for index, (item_id, field_id, option_id) in enumerate(changes)
        ]
        updated = graphql_mutations(operations)
        for index, (item_id, _, _) in enumerate(changes):
            if updated[f"set{index}"]["projectV2Item"]["id"] != item_id:
                raise RuntimeError("Project field response item identity mismatch.")

    ensure_views(project["id"])

    if not project.get("shortDescription"):
        check_graphql_capacity(10)
        gh("project", "edit", number, "--owner", owner,
           "--description", "Praxis product delivery: milestone gates, linked features, verified engineering work.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true",
                        help="Reconcile the authorized GitHub repository and project.")
    args = parser.parse_args()

    plan = json.loads(PLAN_PATH.read_text(encoding="utf-8"))
    items, dependencies = validate(plan)
    print(f"Validated {len(items)} tracked issues, {len(plan['features'])} features, "
          f"{len(plan['milestones'])} milestones and "
          f"{sum(map(len, dependencies.values()))} dependency edges.")
    if not args.apply:
        return

    check_graphql_capacity(20)
    project = verify_target(plan)
    print(f"Authorized target: {plan['repository']} / "
          f"{plan['project']['owner']} project #{plan['project']['number']}")
    ensure_milestones(plan)
    ensure_labels(plan)
    reconcile_issues(plan, items, dependencies)
    issues = {
        issue["number"]: issue
        for issue in gh_json(
            "issue", "list", "-R", plan["repository"], "--state", "all",
            "--limit", "200", "--json", "number,state,id"
        )
    }
    reconcile_project(plan, project, items, issues)
    print("GitHub delivery metadata reconciled.")


if __name__ == "__main__":
    main()
