# GitHub delivery management

GitHub [Praxis Roadmap Project #3](https://github.com/users/berniemackie97/projects/3) is the delivery board; [#1](https://github.com/berniemackie97/Praxis/issues/1) is the program index. The [declarative roadmap](../scripts/github/roadmap.json) maps existing issues to stages, parent features and critical dependencies.

## Execution focus: standalone local Praxis

The repository milestones (M0–M7) classify **acceptance gates**, not the order developers must code them. Reviewed cross-milestone delivery lanes are in [standalone-mvp.md](delivery/standalone-mvp.md) and `scripts/github/roadmap.json`. The first active chain is #99 → #100 → #102/#101 → #103 → #106 → #107 → #108; voice #109/#110, browser #104/#111/#112 and MCP #117–#119 follow as separately testable outcomes. Obsidian #5 is optional and does not block initial direct capture.

Limit work-in-progress to one cohesive implementation slice awaiting merge per active workstream, except independent decision spikes. Each issue is audited against current code before taking it, and every PR reports exact test evidence, unresolved integration gaps and its linked parent issue. Do not close a parent feature because a single child test passes. No remote processing or agent permission is inferred from user-local ingestion.

## Work item hierarchy

| Level | Purpose | Completion condition |
| --- | --- | --- |
| Program | Overall outcomes, engineering guardrails and dependencies | Product objectives are met |
| Milestone | A verifiable release capability gate, M0–M7 | Gate evidence reviewed; no arbitrary completion dates |
| Epic | A major product outcome within a milestone | All features complete and integrated |
| Feature | A cohesive user-facing or operational capability | Child items and end-to-end feature acceptance demonstrated |
| Story | Observable user requirement | Acceptance behavior and regression tests verified in merged code |
| Task | One bounded engineering deliverable, usually one PR | Scope and verification complete; PR merged |
| Spike | Resolve one concrete uncertainty, not write speculative code | Evidence and a recorded decision or rejected option |
| Bug | Reproduced deviation from agreed behavior | Root cause addressed and regression test verified |

Milestones provide delivery gates; the program/epic/feature hierarchy provides breakdown. A feature can span multiple implementation PRs. A label or title is not a substitute for the issue's actual parent and dependency relationships.

Personal repositories do not have organization-configured issue types. Prefixes and \`kind:*\` labels are the stable classification; \`area:*\` labels support domain filtering. No custom field called \`Type\` is needed.

Closed duplicate issues #42–#45 are retained in GitHub's history but excluded from milestone progress and the current delivery hierarchy.

## Status and priority

Use the existing Project **Status** field. Assign Todo/Backlog only to unstarted, unblocked work; In Progress when actively worked; In Review when a PR is awaiting merge (only if that option exists); and Done after the issue is closed with verified acceptance. Blocked work should have a native dependency link and appear in the Decision Queue or a blocker view; do not create a second source of truth by adding ad-hoc blocked labels.

The Project **Stage** field mirrors the milestone stage for board filtering. The reconciliation tool fills missing Stage and Priority values but preserves existing curated open-item values. Reviewed `priority_migrations` in the source plan may promote a specifically enumerated issue **only** when its existing Priority matches the declared prior value. Other custom values remain untouched. A declared dependency-removal list similarly removes only explicitly identified stale blockers.

- **P0:** Release-blocking security, recovery, integrity or immediate critical-path decision.
- **P1:** Required for the next demonstrable delivery milestone.
- **P2:** Planned work that is not currently on the critical path.
- **P3:** Later capabilities; not a judgment of product importance.

Priority reflects risk and execution order, not title level. Operational work may be P0 even if it belongs to the later operations epic.

## Definition of Ready

- One accountable scope with a parent feature or epic.
- Concrete acceptance criteria and meaningful failure/negative cases.
- Known prerequisite issues linked through GitHub dependencies.
- Security/data-handling constraints identified; unknown design choices have an owned spike.
- A reviewable test and delivery strategy, without unnecessary infrastructure commitments.

If an issue does not meet these criteria, it remains backlog/research rather than being implemented on assumptions.

## Definition of Done

- The linked implementation is merged after actual CI and code review.
- Tests validate behavior and relevant failure/security cases, not only a happy-path fixture.
- Data migration, compatibility, authorization, resource ownership and recovery were considered and verified where relevant.
- Documentation and ADRs match the observed implementation.
- No unresolved blocking findings; follow-ups are tracked explicitly.
- User-facing or feature-level exit criteria are demonstrated before closing the feature or milestone.

A green CI check alone is not evidence that a milestone's operational acceptance is complete.

## Flow and review cadence

Use a Kanban flow with a default WIP limit of **one implementation slice** at a time. An independent research spike can progress alongside implementation when it does not compromise review capacity. This is a prioritization rule, not an invented sprint schedule.

At each merged PR: reconcile issue closure, dependencies, project status and milestone gates; inspect newly found risks; select the highest-priority Ready work item. Periodically audit the backlog for duplicates, stale blocked states, excessive work-in-progress and unmeasured acceptance.

Recommended Project views: Delivery Board (active issues), Feature Portfolio, Decision Queue, Defect Triage and Milestone Portfolio. Use native milestones and sub-issue/dependency links. Do not fabricate release dates or report a planned client/platform as supported.

## Administrative synchronization

GitHub's repository connector does not manage Projects fields, views, milestones or sub-issue relationships. The repository includes an owner-run [idempotent configuration tool](../scripts/github/plan.py) using authenticated GitHub CLI. See [setup and verification](project-setup.md).

The tool only fills missing issue/project metadata, creates required labels/milestones/views, and converts the audited JSON mapping into native GitHub relationships. It does not create feature issues, merge PRs, change issue descriptions, delete labels, or overwrite curated open-item project statuses. A committed change to the mapping requires normal PR review.
