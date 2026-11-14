#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../../.." && pwd)"
source "$root/scripts/macos/xcode-toolchain.sh"
source_dir="$root/scripts/macos/GraphRendererBenchmark"
output="$root/.artifacts/graph-renderer-benchmark"
mkdir -p "$(dirname "$output")"
xcrun swiftc -O -parse-as-library \
  "$source_dir"/*.swift \
  -o "$output"
printf 'Built %s\n' "$output"
printf 'Run: %s /path/to/synthetic-case.json\n' "$output"
