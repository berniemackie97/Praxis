#!/usr/bin/env python3
"""Validate or reconcile the repository's GitHub delivery metadata."""

import argparse
import json
import re
import subprocess
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

    dependencies = {int(issue): set(blockers) for issue, blockers in plan["dependencies"].items()}
    for number, blockers in dependencies.items():
        if number not in items or number in blockers or not blockers.issubset(items):
            raise ValueError(f"Invalid dependency edge on #{number}.")

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
        if number == 50:
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

        blocked_by = {entry["number"] for entry in issue.get("blockedBy") or []}
        for dependency in sorted(dependencies.get(number, ()) - blocked_by):
            gh("issue", "edit", number, "-R", repo, "--add-blocked-by", dependency)

        print(f"Reconciled #{number}: {kind} / {stage or 'portfolio'}")


def ensure_views(project_id):
    query = """query($projectId: ID!) {
      node(id: $projectId) {
        ... on ProjectV2 { views(first: 50) { nodes { id name } } }
      }
    }"""
    data = gh_json("api", "graphql", "-f", f"query={query}", "-f",
                   f"projectId={project_id}")
    existing = {
        view["name"] for view in data["data"]["node"]["views"]["nodes"]
    }
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
        if name in existing:
            continue
        result = gh_json(
            "api", "graphql", "-f", f"query={create}", "-f",
            f"projectId={project_id}", "-f", f"name={name}", "-f", f"layout={layout}"
        )
        view_id = result["data"]["createProjectV2View"]["projectV2View"]["id"]
        gh("api", "graphql", "-f", f"query={update}", "-f", f"id={view_id}",
           "-f", f"filter={filter_query}")
        print(f"Created view: {name}")


def reconcile_project(plan, project, items, issues):
    repo = plan["repository"]
    owner, number = plan["project"]["owner"], plan["project"]["number"]

    fields = gh_json("project", "field-list", number, "--owner", owner,
                     "--format", "json", "--limit", "100")["fields"]
    by_name = {field["name"]: field for field in fields}
    if not {"Stage", "Priority", "Status"}.issubset(by_name):
        raise RuntimeError("Project must contain Stage, Priority and Status fields.")
    for field_name, required in (
        ("Stage", {f"M{i}" for i in range(8)}),
        ("Priority", {"P0", "P1", "P2", "P3"}),
    ):
        options = {value["name"] for value in by_name[field_name].get("options", [])}
        if options and not required.issubset(options):
            raise RuntimeError(f"{field_name} field is missing required options.")

    existing = gh_json("project", "item-list", number, "--owner", owner,
                       "--format", "json", "--limit", "200",
                       "--field", "Stage", "--field", "Priority",
                       "--field", "Status")["items"]
    project_items = {
        entry["content"]["url"]: entry
        for entry in existing if isinstance(entry.get("content"), dict)
        and entry["content"].get("url")
    }
    statuses = {value["name"] for value in by_name["Status"].get("options", [])}
    backlog = next((value for value in ("Todo", "Backlog", "Ready")
                    if value in statuses), None)

    for issue_number, metadata in sorted(items.items()):
        url = f"https://github.com/{repo}/issues/{issue_number}"
        entry = project_items.get(url)
        if entry is None:
            entry = gh_json("project", "item-add", number, "--owner", owner,
                            "--url", url, "--format", "json")
            print(f"Added to board: #{issue_number}")

        for field, value in (
            ("Stage", metadata["stage"]),
            ("Priority", metadata["priority"]),
        ):
            if value is None:
                continue
            current = entry.get(field, entry.get(field.lower()))
            if isinstance(current, dict):
                current = current.get("name")
            if current == value:
                continue
            if current:
                print(f"Retained existing {field}={current} on #{issue_number}")
                continue
            gh("project", "item-edit", number, "--owner", owner,
               "--url", url, "--field", field, "--value", value)

        current_status = entry.get("status", entry.get("Status"))
        if isinstance(current_status, dict):
            current_status = current_status.get("name")
        target_status = "Done" if issues[issue_number]["state"] == "CLOSED" else backlog
        if target_status and target_status in statuses and (
            not current_status or
            (issues[issue_number]["state"] == "CLOSED" and current_status != "Done")
        ):
            gh("project", "item-edit", number, "--owner", owner,
               "--url", url, "--field", "Status", "--value", target_status)

    ensure_views(project["id"])

    if not project.get("description"):
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
            "--limit", "200", "--json", "number,state"
        )
    }
    reconcile_project(plan, project, items, issues)
    print("GitHub delivery metadata reconciled.")


if __name__ == "__main__":
    main()
