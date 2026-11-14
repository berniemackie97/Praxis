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
dotnet="${PRAXIS_DOTNET_EXECUTABLE:-}"
if [[ -z "$dotnet" ]]; then
  dotnet="$(command -v dotnet || true)"
fi
if [[ -z "$dotnet" || ! -x "$dotnet" ]]; then
  echo "The .NET 10 SDK was not found for hosted worker tests." >&2
  echo "Set PRAXIS_DOTNET_EXECUTABLE to the installed dotnet executable." >&2
  exit 1
fi

PRAXIS_WORKER_DLL="$worker" PRAXIS_DOTNET="$dotnet" \
  xcodebuild -quiet \
    -project "$PRAXIS_XCODE_PROJECT" \
    -scheme "$PRAXIS_XCODE_SCHEME" \
    -configuration Debug \
    -destination 'platform=macOS' \
    -derivedDataPath "$derived" \
    CODE_SIGNING_ALLOWED=NO \
    test
