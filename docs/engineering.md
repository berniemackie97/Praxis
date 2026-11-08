# Engineering and delivery workflow

1. Audit the current repository, linked issue, dependencies, code paths, tests, CI, and review feedback.
2. Define the smallest complete outcome with acceptance criteria, failure cases, and a reviewable scope.
3. Verify relevant external behavior, constraints, and design alternatives before writing production code.
4. Implement focused responsibilities and meaningful success/failure-path tests; do not introduce unused abstractions.
5. Audit correctness, security, concurrency, ownership, latency, persistence integrity, reliability, and maintainability.
6. Run relevant build, tests, formatting, and integration verification. Distinguish actual results from unrun checks.
7. Push a dedicated branch and open a linked PR. Review its diff, CI, and comments; fix findings and re-audit.
8. Leave merge decisions to the repository owner. After merge, reconcile roadmap items and choose the next slice.

## Code standards

- Eliminate duplicated parsing and data-flow logic; keep one clear owner for each responsibility.
- Avoid god classes, speculative extension points, forwarding wrappers, broad catch-and-ignore blocks, redundant checks, dead code, and commentary that repeats the implementation.
- Specify bounded resources, cancellation, retries, lifetime ownership, and observed failure behavior where applicable.
- Prefer measurable behavior and small changes over premature distributed systems or framework layering.
- No secrets, access tokens, or personal source data in commits, logs, tests, or issue reproductions.
- ADRs describe durable decisions and rejected alternatives, not implementation narration.

## PR gate

A PR is ready for owner review only when its actual relevant tests and CI are green, its diff has been audited, linked issues have clear disposition, and remaining risks are called out. Never treat unrun tests as passed or merge without the owner's direction.

## Dependency and workflow security

- CI pins referenced GitHub Actions to reviewed commit hashes. Dependabot proposes updates; review the new commit and its permissions before merging.
- Restore audits direct and transitive NuGet dependencies, including low-severity advisories, with warnings treated as errors. Investigate the originating package and fix or replace it; advisory suppression requires a specific, documented risk decision.
- CI uses the Ubuntu 24.04 runner family rather than the moving `ubuntu-latest` alias. Dependabot checks NuGet and workflow actions weekly; updates are reviewable PRs, not automatic merges.
- The SDK can roll forward within the configured feature-band policy; package lock files and reproducible restore are not yet established. NuGet package lock files are committed for every solution project, and CI uses `dotnet restore Praxis.slnx --locked-mode` so dependency drift fails before build. They do not by themselves pin the SDK or make the complete release reproducible. Database migrations and rollback rehearsals remain #46 follow-up work once persistent schema exists.

## Updating NuGet lock files

The central package versions are in `Directory.Packages.props`, while each project keeps its resolved `packages.lock.json` beside its `.csproj`. After deliberately changing a package reference or central version, run `dotnet restore Praxis.slnx --force-evaluate`, review every lockfile diff (including transitive changes and content hashes), then verify `dotnet restore Praxis.slnx --locked-mode` and run the normal tests. Never manually edit a package content hash or disable the dependency audit to make a restore pass.

NuGet warns that SDK updates can change implicit package references, so these locks establish an explicit *package graph* at the tested SDK, not a fully pinned build toolchain. The current `global.json` allows SDK feature-band roll-forward; selecting and validating a specific supported SDK patch and proving release/rollback reproducibility remain under #46.
