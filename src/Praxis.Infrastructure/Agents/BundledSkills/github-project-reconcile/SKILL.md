---
name: github-project-reconcile
description: "Reconcile GitHub boards, roadmap issues, PRs, blockers and release acceptance gates."
---

Identify the exact project, repositories, and scoped items. Compare current cards and source issues by repository plus number or stable ID; include open PRs, prerequisites, milestones and checked gates. A merged PR can be done while its issue still needs verification. Propose only evidenced moves and prefer existing issues to duplicates. Re-read after interrupted changes before retrying. If GitHub access is unavailable, use supplied exports and return a proposed change list with links and missing evidence.
