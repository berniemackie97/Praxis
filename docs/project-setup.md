# GitHub project administration

The issue breakdown exists in the repository already. [Praxis Roadmap Project #3](https://github.com/users/berniemackie97/projects/3) is a private user-owned project linked to \`berniemackie97/Praxis\`.

The repository integration can create/update issues and PRs but does not administer project views, repository milestone definitions or issue relationships. Configure those using the owner's authenticated GitHub CLI; do not put access tokens in files or CI secrets.

## Requirements

- Python 3.11 or later.
- GitHub CLI 2.94.0 or later, authenticated as \`berniemackie97\`, with repository administration and \`project\` scope.
- Current \`main\` checkout with [roadmap.json](../scripts/github/roadmap.json). The command validates the owner/repository/project identity before applying changes.

Run on your Mac:

\`\`\`sh
cd ~/Praxis
git switch main
git pull --ff-only
gh auth refresh -s project
python3 scripts/github/plan.py
python3 scripts/github/plan.py --apply
\`\`\`

\`--apply\` creates missing milestones, labels, issue parent/dependency links, board items and views. It assigns missing Stage/Priority metadata and aligns closed issues to Done when that status exists. It never adds another user or collaborator and never merges a PR.

The script can be rerun after interruption. It preserves nonempty user-defined priority/stage fields, existing issue milestones and parent links, and already created views. If a conflict is discovered, execution stops rather than overwriting the existing configuration. The command's progress output indicates where to resume.

## Verification

\`\`\`sh
gh api "repos/berniemackie97/Praxis/milestones?state=all&per_page=100" \
  --jq '.[] | [.title, .open_issues, .closed_issues] | @tsv'

gh project field-list 3 --owner berniemackie97
gh project item-list 3 --owner berniemackie97 --limit 100
gh issue view 54 -R berniemackie97/Praxis --json parent,subIssues,milestone
gh issue view 19 -R berniemackie97/Praxis --json blockedBy
gh project view 3 --owner berniemackie97 --web
\`\`\`

Expected: eight distinct M0–M7 milestones, fourteen feature issues, native roadmap/epic/feature/child relationships, a linked critical path and classified board items. The current plan tracks 59 unique nonduplicate issues, including the program index; the number changes only with reviewed roadmap changes.

GitHub Projects currently reserves the field name \`Type\`; kind labels plus issue title prefixes cover personal repository classification. Native organization issue types are not required.

## Views and automation boundaries

The setup creates project views but does not assign fictional target dates, turn on untrusted automation, or install integrations. Group the Delivery Board by Status and the Milestone Portfolio by Stage in the Projects UI if preferred; the source-of-truth milestones remain GitHub repository milestones.

Automatic issue-close workflows may be configured using GitHub's built-in project automation. Do not introduce a write-scoped personal access token into Actions merely to update a board.
