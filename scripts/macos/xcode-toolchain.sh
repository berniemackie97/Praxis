#!/usr/bin/env bash
# Require a full Xcode installation for native app builds and Swift Testing.
set -euo pipefail

if ! xcrun --find xcodebuild >/dev/null 2>&1; then
  echo "Select a full Xcode installation before building Praxis." >&2
  echo "Example: sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer" >&2
  exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export PRAXIS_XCODE_PROJECT="$root/apps/macos/Praxis.xcodeproj"
export PRAXIS_XCODE_SCHEME=Praxis
