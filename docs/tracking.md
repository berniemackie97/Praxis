# GitHub work item taxonomy

- **Roadmap:** the cross-stage index and dependency order.
- **Epic:** a meaningful outcome that needs multiple independently reviewable items. Remains open until all exit criteria are satisfied.
- **Story:** user-observable behavior with acceptance and regression criteria.
- **Task:** bounded engineering delivery, normally associated with one PR.
- **Spike:** targeted research to resolve an explicit decision and record evidence.
- **Bug:** a reproduced incorrect behavior with evidence, expected/actual behavior, and regression coverage.

Use issue title prefixes to distinguish types without requiring external software. Existing GitHub labels may provide secondary classification. A rejected or hypothetical behavior is a risk or spike, not a bug.

## GitHub Project

The private [Praxis Roadmap](https://github.com/users/berniemackie97/projects/3) project is linked to the repository. Initial work items and the foundation PR were submitted to the board.

Use its built-in Status and issue classifications where available. Custom fields Stage (M0–M7) and Priority (P0–P3) were created. An additional custom field named Type was rejected as a reserved name; issue title prefixes remain the stable type convention.

Suggested views: roadmap by Stage, active work by Status, blocked, bugs, and release gate. Project field values and views require explicit configuration; do not infer their state from issue titles.

GitHub issues remain authoritative for acceptance criteria, dependencies, closure and PR relationships even if the board is temporarily unavailable.

## Completion

Close a task or story only after associated PR merges and all acceptance criteria are met. Epic closure follows successful integration and verified stage exit gates. Document blockers and follow-up work as linked issues.
