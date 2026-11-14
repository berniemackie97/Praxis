#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/../../.." && pwd)"
source "$root/scripts/macos/xcode-toolchain.sh"

source_dir="$root/scripts/macos/GraphRendererBenchmark"
output="$root/.artifacts/graph-scene-smoke"
mkdir -p "$(dirname "$output")"

xcrun swiftc -O -swift-version 6 -parse-as-library \
  -target "$(uname -m)-apple-macos13.0" \
  "$source_dir/GraphFixture.swift" \
  "$source_dir/GraphPositions.swift" \
  "$source_dir/SpriteGraphView.swift" \
  "$root/scripts/macos/GraphSceneSmoke.swift" \
  -o "$output"

"$output" "$@"
