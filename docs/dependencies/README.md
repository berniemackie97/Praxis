# Resolved NuGet dependencies and license review

The checked-in [inventory](nuget-inventory.json) records every external package resolved across the Praxis solution using the exact SDK in `global.json`, the committed NuGet lockfiles and the package manifests installed by NuGet. Direct/transitive versions, SHA-512 content hashes, consuming projects and publisher-declared license metadata are included.

The inventory is a **review aid, not legal approval or an independent license audit**. Package metadata can be inaccurate, legacy license URLs can change, and transitive dependencies need their own review. At the current baseline, 43 packages declare an expression of MIT or Apache-2.0. The remaining `xunit.abstractions` 2.0.3 declares only a URL (`https://raw.githubusercontent.com/xunit/xunit/master/license.txt`); it must be independently reviewed against its released artifact, not silently classified as approved.

CI restores the committed package graph in locked mode and regenerates the report from the actual package cache, failing if names, versions, integrity hashes, project usage or license declarations differ. An intentional package update requires:

1. Update `Directory.Packages.props` and any direct `PackageReference` changes, keeping the exact SDK specified by `global.json`.
2. Regenerate lockfiles using `dotnet restore Praxis.slnx --force-evaluate`, and review resolved dependency and content-hash changes.
3. Run `python3 scripts/release/dependency_inventory.py --write` against the restored package cache and review the generated diff, particularly any new license expressions, file licenses, legacy URLs or missing metadata.
4. Verify `dotnet restore Praxis.slnx --locked-mode`, `python3 scripts/release/dependency_inventory.py --check`, the release tests, and the build pipeline before merging.

Do not treat absence of an advisory as a security guarantee. The configured NuGet audit checks known advisories against the current advisory source during restore. It does not validate upstream code, package signatures, supply-chain provenance, or license compatibility.

Release approval must record the human review of nontrivial licenses and any exceptions. No automatic approval of all MIT/Apache declarations is implied by these checks.
