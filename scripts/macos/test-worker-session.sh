#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
source "$root/scripts/macos/xcode-toolchain.sh"
derived="$root/.artifacts/XcodeDerivedData"
worker="$root/.artifacts/Praxis.app/Contents/Resources/worker/Praxis.Worker.dll"

if [[ ! -f "$worker" ]]; then
  echo "Run scripts/macos/build-desktop.sh to package the .NET worker first." >&2
  exit 1
fi
if ! command -v dotnet >/dev/null 2>&1; then
  echo "The .NET 10 SDK is required for the worker integration test." >&2
  exit 1
fi

PRAXIS_WORKER_DLL="$worker" PRAXIS_DOTNET="$(command -v dotnet)" \
  xcodebuild \
    -project "$PRAXIS_XCODE_PROJECT" \
    -scheme "$PRAXIS_XCODE_SCHEME" \
    -configuration Debug \
    -destination 'platform=macOS' \
    -derivedDataPath "$derived" \
    CODE_SIGNING_ALLOWED=NO \
    test
