# SDK and release verification

Praxis uses the exact .NET SDK `10.0.403` with `global.json` roll-forward disabled. Install that SDK before running the solution locally; newer SDKs installed alongside it will **not** be substituted automatically. CI installs the version selected in `global.json` using the pinned setup-dotnet action.

The required pull-request pipeline runs locked NuGet restore, Release build and .NET tests on Ubuntu 24.04, macOS 15 (Apple Silicon runner) and Windows Server 2025. The Linux job also checks formatting, planning validation, dependency inventory and the published API's HTTP liveness after launching its output as a separate local process.

These checks establish a consistent SDK, dependency graph and baseline portability for the current source. They do **not** establish byte-identical published binaries across operating systems, guarantee offline restore with no package cache, exercise client filesystem adapters on real devices, or validate database migrations/rollbacks before a schema exists.

Before approving a release, compare the new [NuGet dependency/license inventory](../dependencies/README.md), resolve advisory and license exceptions, verify that supported operating systems pass CI, and confirm separate migration, recovery, privacy and deployment gates. Additional platform coverage and host-specific packaging require evidence tied to supported release targets.

To upgrade the SDK intentionally, update `global.json` in a reviewed PR, install the candidate in CI on each target OS, regenerate any affected NuGet locks and inventory, then re-run the full matrix. Do not relax SDK roll-forward or turn off locked restore as an upgrade workaround.
