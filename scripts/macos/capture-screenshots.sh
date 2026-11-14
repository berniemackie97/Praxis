#!/usr/bin/env bash
# Renders every workbench workspace and settings tab against a real worker and a fixture
# library, in light and dark appearance. Output: .artifacts/screenshots (or the first argument).
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
source "$root/scripts/macos/xcode-toolchain.sh"
derived="$root/.artifacts/XcodeDerivedData"
output="${1:-$root/.artifacts/screenshots}"
dotnet="${PRAXIS_DOTNET_EXECUTABLE:-$(command -v dotnet || true)}"
if [[ -z "$dotnet" || ! -x "$dotnet" ]]; then
  echo "The .NET 10 SDK was not found. Set PRAXIS_DOTNET_EXECUTABLE." >&2
  exit 1
fi

mkdir -p "$output"
TEST_RUNNER_PRAXIS_SNAPSHOT_DIR="$output" TEST_RUNNER_PRAXIS_DOTNET="$dotnet" \
  xcodebuild -quiet \
    -project "$PRAXIS_XCODE_PROJECT" \
    -scheme "$PRAXIS_XCODE_SCHEME" \
    -configuration Debug \
    -destination 'platform=macOS' \
    -derivedDataPath "$derived" \
    -only-testing:PraxisTests/ScreenshotTests \
    CODE_SIGNING_ALLOWED=NO \
    test
echo "Screenshots written to $output"
