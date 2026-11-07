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
