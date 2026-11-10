#!/usr/bin/env bash
# Shared by the macOS scripts: require the full Xcode toolchain (Swift Testing, macOS SDK and
# xcodebuild), not the standalone Command Line Tools. DEVELOPER_DIR, when set, is honored.
set -euo pipefail

if ! xcrun --find xcodebuild >/dev/null 2>&1; then
  echo "The macOS workbench requires Xcode as the active developer directory." >&2
  echo "Run: sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer" >&2
  echo "Or set DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer for this shell." >&2
  exit 1
fi

package="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../apps/macos" && pwd)"
export PRAXIS_SWIFT_PACKAGE="$package"
