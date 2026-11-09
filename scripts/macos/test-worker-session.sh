#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
worker="$root/.artifacts/Praxis.app/Contents/Resources/worker/Praxis.Worker.dll"
if [[ ! -f "$worker" ]]; then
  echo "Build the macOS workbench before running the worker session smoke test." >&2
  exit 1
fi

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

swiftc -swift-version 6 -warnings-as-errors -parse-as-library \
  "$root"/apps/macos/Worker/*.swift \
  "$root/apps/macos/Tests/WorkerSessionSmoke.swift" \
  -o "$scratch/worker-session-smoke"
"$scratch/worker-session-smoke" "$(command -v dotnet)" "$worker"
