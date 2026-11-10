#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
source "$root/scripts/macos/xcode-toolchain.sh"
worker="$root/.artifacts/Praxis.app/Contents/Resources/worker/Praxis.Worker.dll"
if [[ ! -f "$worker" ]]; then
  echo "Build the macOS workbench before running the worker session test." >&2
  exit 1
fi

# Unit tests always run; the worker integration suite runs because the worker is provided.
PRAXIS_WORKER_DLL="$worker" PRAXIS_DOTNET="$(command -v dotnet)" \
  swift test --package-path "$PRAXIS_SWIFT_PACKAGE"
