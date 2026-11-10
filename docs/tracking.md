# GitHub delivery management

GitHub [Praxis Roadmap Project #3](https://github.com/users/berniemackie97/projects/3) is the delivery board; [#1](https://github.com/berniemackie97/Praxis/issues/1) is the program index. The [declarative roadmap](../scripts/github/roadmap.json) maps existing issues to stages, parent features and critical dependencies.

## Execution focus: foundational local Praxis → integrated intelligent MVP

Milestones M0–M7 remain capability gates rather than chronological work queues. [The delivery plan](delivery/standalone-mvp.md) separates the **local foundation #130** from the **integrated intelligent MVP #170**. Merged implementation includes the private source catalog, watched intake, original recovery, FTS5 and a first-party SwiftUI Xcode application (#144–#220). The current app additionally contains owner collections, quick note and source export, Ollama-backed proposals and typed entities/relations, as well as owner-approved stronger-model verification. Those slices do **not** establish complete hardware, release, correctness or agent/task acceptance.

**Next active gate (November 10, 2026):** #215 is P1 under first-party feature #64 and blocks #130 until current `main` builds, launches and operates through the actual Xcode app on the owner's Mac. The merged fixes #216–#220 address known project/compiler/publish failures but have no reported successful post-fix GUI/worker run. CI #137 is blocked by deferred runner provisioning; no queued workflow is a pass. #219 is the P2 M0 documentation/status reconciliation, not a substitute for hardware verification. The versioned [project plan](../scripts/github/roadmap.json) tracks #215 and #219 without changing unrelated priority fields.

The current intelligence work (#115/#116/#31) is partially delivered in merged #165/#204–#212 but remains open for independent quality, denial and source-revocation tests. #172 is a measured retrieval/provider-selection decision, not complete because preliminary local Qwen/Codex/Claude timings exist. The actual #170 release still requires cross-domain grounded chat/tasks (#29/#33/#150), directly playable cited media (#177), durable financial reconciliation and an editable source-backed budget (#178), scoped approvals (#32/#34) and Obsidian-derived views (#171). MCP, remote sync and browser/voice connections keep their own trust gates.

Limit active implementation to one cohesive PR per workstream. Before selecting a new task, reconcile live issues, merged PRs, blockers, the plan and available tests; never close a parent feature based only on a child PR. No source import or model text grants network/tool authority.

## Scope and priority correction — November 9, 2026

The owner requires **agent-assisted knowledge synthesis and task execution in the MVP**, not as a loosely specified later phase. #130 is relabeled as the local foundational acceptance gate; #170 is the actual cross-stage product release gate. Preserve both. The MVP includes evidence-linked technical interpretation (#149/#150), reviewed knowledge #115/#116, evaluated adaptive retrieval/model escalation (#29/#31/#172), first-party chat/tasks (#33/#32/#34/#173), permissioned context projections (#143), and safe Obsidian Markdown knowledge views (#57/#171). Scope hardware and corpus, not reliability or safety standards.

P1 means critical path for this **actual MVP**, even where an issue belongs to M3/M5. Lanes L0 and L4 feed the integrated release lane L6; unrelated bank/social connectors, remote sync and voice conversation remain separately gated. Promoting a planned issue to MVP does **not** mark its code complete or waive acceptance.

A model cannot be a security authority. Every provider/adapter is selected by supported integration path, privacy scope, measured quality, budget and permissions. Independent, exact technical evidence and user-visible approvals are required before claiming an answer or action is correct.

## Cross-domain MVP correction

The owner requires that the first intelligent release be **general-purpose**, not only a technical research assistant. L4 remains a technical test slice, while new feature **#176** ensures L6/#170 additionally demonstrates supported video tutorial retrieval with direct in-chat playback and full-source timestamp navigation (#177), as well as reconciled financial data analysis and an editable deterministic monthly budget (#178). Cross-domain evaluation #179 tests all three with grounded citations, ambiguity, source revocation, privacy and numerical accuracy. Real-time bank connections and universal file/video comprehension are *not* implied by this representative, production-quality MVP.

The evidence model must allow selectors suited to each modality: code symbol/span/revision, text paragraph, video start/end/source timebase, and structured financial record/field. Provider-known transaction types are deterministic; AI enrichment remains reviewable and permissioned. This expands the product's required workflows, not the authority of its models or the scope of its accounts.

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

GitHub's repository connector does not manage Projects fields, views, milestones or sub-issue relationships. Changes to the live private Project #3 cannot be inferred from repository issue edits. After merging a validated roadmap PR, run the owner-side reconciler and inspect field values and parent/dependency links; no claim of a synchronized board is made without that evidence.

The repository includes an owner-run [idempotent configuration tool](../scripts/github/plan.py) using authenticated GitHub CLI. See [setup and verification](project-setup.md).

The tool only fills missing issue/project metadata, creates required labels/milestones/views, and converts the audited JSON mapping into native GitHub relationships. It does not create feature issues, merge PRs, change issue descriptions, delete labels, or overwrite curated open-item project statuses. A committed change to the mapping requires normal PR review.
