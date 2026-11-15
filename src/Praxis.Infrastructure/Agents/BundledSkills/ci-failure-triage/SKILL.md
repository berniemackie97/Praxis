---
name: ci-failure-triage
description: "Diagnose GitHub Actions or other CI failures from job logs, commits, and reproduction evidence."
---

Identify the exact job, run, commit and matrix entry. Read the first causal failure and compare with an earlier successful run; distinguish an infrastructure flake from a regression. Suggest a local reproduction for that target and a minimal fix with verification. If the job provider is unavailable, request the failed step log and avoid attributing a root cause from status alone.
