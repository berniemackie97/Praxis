# GitHub work item taxonomy

- **Roadmap:** the cross-stage index and dependency order.
- **Epic:** a meaningful outcome that needs multiple independently reviewable items. Remains open until all exit criteria are satisfied.
- **Story:** user-observable behavior with acceptance and regression criteria.
- **Task:** bounded engineering delivery, normally associated with one PR.
- **Spike:** targeted research to resolve an explicit decision and record evidence.
- **Bug:** a reproduced incorrect behavior with evidence, expected/actual behavior, and regression coverage.

Use issue title prefixes to distinguish types without requiring external software. Existing GitHub labels may provide secondary classification. A rejected or hypothetical behavior is a risk or spike, not a bug.

## Suggested board views

GitHub Projects fields: Status (Backlog, Ready, In progress, In review, Blocked, Done), Priority (P0, P1, P2), Stage (M0–M7), and Type (Epic, Story, Task, Spike, Bug). Views: roadmap, active sprint, blocked, bugs, release gate.

Board fields and repository labels must be configured through the GitHub administration interface. Issue titles and links remain authoritative even without Projects features.

## Completion

Close a task or story only after associated PR merges and all acceptance criteria are met. Epic closure follows successful integration and verified stage exit gates. Document blockers and follow-up work as linked issues.
