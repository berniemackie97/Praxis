# Zero-charge GitHub Actions CI for private Praxis

**Scope:** [#137](https://github.com/berniemackie97/Praxis/issues/137). This is a proposed alternative to billed GitHub-hosted runners, not a claim that any self-hosted runner has been registered or any tests passed.

## Decision and cost boundary

The repository stays **private**. The CI workflow uses only GitHub Actions **self-hosted** runners. GitHub's current [Actions billing documentation](https://docs.github.com/en/actions/concepts/billing-and-usage) states that self-hosted execution is free and does not consume included compute. GitHub [postponed the announced charge](https://github.blog/changelog/2025-12-16-coming-soon-simpler-pricing-and-a-better-experience-for-github-actions/) for self-hosted runners. Electricity, hardware and a machine that remains online are owner responsibilities. There is no paid hosted-runner fallback.

**Do not assume** the billing-restricted hosted runner can be fixed by changing the YAML. The original failed run was explicitly refused for account payment/spending reasons before any job executed. The new workflow has a separate operating prerequisite: suitable registered self-hosted workers must be online.

## Provisioning — owner action, required once

1. Open [Praxis → Settings → Actions → Runners](https://github.com/berniemackie97/Praxis/settings/actions/runners) and choose **New self-hosted runner**. Use the **current instructions and runner package** GitHub presents for each operating system and architecture; registration tokens are short-lived and must not be checked into the repository, saved in logs or pasted into chat.
2. Register **three independently identified OS workers**, with the following custom label on each. Use a dedicated low-privilege runner account and preferably a disposable VM/host, not the owner's everyday login or any machine containing sensitive source material. Label names are case-insensitive in GitHub's runner scheduler.

   | Workflow check | Required custom runner label | Host requirement |
   | --- | --- | --- |
   | Verify Linux | `praxis-linux` | Linux runner (Ubuntu 24.04 recommended for original coverage) |
   | Verify macOS | `praxis-macos` | macOS runner, including an up-to-date Apple Silicon Mac |
   | Verify Windows | `praxis-windows` | Windows runner (Windows Server 2025 recommended for original coverage) |

   During registration, assign only the relevant custom label (e.g. GitHub's `./config.sh --labels praxis-macos` on macOS/Linux or `.\config.cmd --labels praxis-windows` on Windows). Default labels `self-hosted`, OS and architecture are added by GitHub automatically. Do not put multiple Praxis OS labels on the same worker; labels are routing hints, not OS virtualization or actual OS validation.
3. Ensure each worker has network access to GitHub and the package feeds, **.NET 10 SDK 10.0.403** (the pinned `global.json` version; `actions/setup-dotnet` also installs it), a compatible Node.js runtime, and the Python 3 toolchain needed for platform-specific checks. On Unix, the command must be `python3`. Do not store private API data, original sources or production credentials in the runner workspace.
4. Start the worker process using the GitHub-provided `run.sh` / `run.cmd` command. Keep it online when CI is needed, or configure an isolated service account according to the official runner installation instructions. The runner auto-update mechanism must remain functional; outdated runner versions are rejected.
5. Inspect the labels and status using the GitHub owner CLI:

   ```sh
   gh api repos/berniemackie97/Praxis/actions/runners \
     --jq '.runners[] | [.name, .status, (.labels | map(.name) | join(","))] | @tsv'
   ```

   Confirm exactly the intended label maps to an **online**, actual target OS worker. The workflow verifies the GitHub-reported actual OS family before checkout, and the code review requirement is the same whether the runner is hosted or self-hosted.

**Using only a Mac:** A single macOS runner can run the macOS job, but **cannot establish Windows or Linux platform parity**. The other two jobs will remain queued until their correctly labeled workers are online. Do not mark CI green, remove required checks, or pretend the missing OS tests passed. Linux virtual machines and Windows machines/VMs can be provided later using existing resources; this design does not authorize paid hardware, operating-system licenses or commercial VM subscriptions.

## Running and verifying

Once three workers are online, open the Actions tab for the workflow or dispatch from the owner CLI against the PR branch:

```sh
gh workflow run ci.yml --ref fix/zero-cost-self-hosted-ci
gh run list --workflow ci.yml --limit 5
```

On a PR, normal `pull_request` events start the same matrix. The `push: main` path runs after merge. **A real green result** requires all three jobs to execute and complete successfully, including restore/build/tests, Node integration and packaging tests, and the Linux-only release/format/roadmap/security checks. Capture the exact SHA, workflow run URL, job names, and test output under #137. Once this is demonstrated, reconcile #129's merged ADR tests and #106's merged search tests using a green commit that actually contains both.

`workflow_dispatch` is enabled to rerun checks without fabricating a source change. **If branch protection requires the old check names**, update it only after the replacement jobs have successfully executed: require `Verify Linux`, `Verify macOS` and `Verify Windows`. These denote real OS-family executions, not fixed image versions. Do not drop a required check just to permit a merge.

## Security and capacity boundaries

- Self-hosted runners execute repository workflow steps and checked-out code **with the worker OS account's permissions**. Treat PR code as potentially untrusted. The first workflow step explicitly **fails** if a PR comes from another repository, before checkout or project code is executed; this is an additional guard, not a sandbox for an untrusted same-repository collaborator.
- Run under a dedicated **non-admin** OS account with minimum access, workspace-only permissions and **no secrets**, production catalog or ordinary personal home data. A dedicated machine or disposable VM is preferable to using a developer's daily-driver account.
- Do not enable SSH sharing, LAN services, private-content endpoints or cloud uploads just to run CI. The worker makes outbound connections to GitHub, not a new owner-local Praxis content listener.
- Avoid stale concurrent builds: the workflow cancels superseded runs for the same branch or PR; it never substitutes skipped checks for required cross-platform checks.
- If the owner has only one runner available, run local checks for immediate development feedback but **retain** the unresolved cross-platform verification gate instead of waiving it. For a free offline alternative independent of GitHub dispatch, invoke the same `dotnet restore/build/test` and Node/Python checks directly on available hosts and record local output honestly; local output is not a GitHub check.
- This does not resolve the **original GitHub billing account restriction**. The GitHub-hosted runner rejection remains correctly diagnosed; the new CI simply avoids billed runner compute. GitHub Support or an owner-approved billing change may still be needed if account policy also prevents self-hosted jobs.
