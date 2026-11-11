#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
source "$root/scripts/macos/xcode-toolchain.sh"
artifacts="$root/.artifacts"
destination="$artifacts/Praxis.app"
derived="$artifacts/XcodeDerivedData"

if [[ $# -ne 0 && ( $# -ne 1 || "${1:-}" != "--archive-existing" ) ]]; then
  echo "Usage: scripts/macos/build-desktop.sh [--archive-existing]" >&2
  exit 2
fi

mkdir -p "$artifacts"
if [[ -e "$destination" || -L "$destination" ]]; then
  if [[ "${1:-}" != "--archive-existing" ]]; then
    echo "Destination already exists: $destination" >&2
    echo "Use --archive-existing to preserve the previous bundle before building." >&2
    exit 1
  fi
  mkdir -p "$artifacts/previous-builds"
  archive="$(mktemp -d "$artifacts/previous-builds/Praxis.XXXXXX")"
  mv "$destination" "$archive/Praxis.app"
  echo "Preserved previous development app: $archive/Praxis.app"
fi
xcodebuild \
  -project "$PRAXIS_XCODE_PROJECT" \
  -scheme "$PRAXIS_XCODE_SCHEME" \
  -configuration Release \
  -destination 'platform=macOS' \
  -derivedDataPath "$derived" \
  CODE_SIGNING_ALLOWED=NO \
  build

built="$derived/Build/Products/Release/Praxis.app"
plist="$built/Contents/Info.plist"
worker="$built/Contents/Resources/worker/Praxis.Worker.dll"
if [[ ! -f "$plist" || ! -f "$worker" ]]; then
  echo "The Xcode build did not produce an app with its published .NET worker." >&2
  exit 1
fi
plutil -lint "$plist"
identifier="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$plist")"
if [[ "$identifier" != 'dev.praxis.renames-preview' ]]; then
  echo "Unexpected development app bundle identifier: $identifier" >&2
  exit 1
fi
minimum="$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$plist")"
if [[ "$minimum" != '13.0' ]]; then
  echo "Unexpected deployment minimum: $minimum (expected macOS 13.0)" >&2
  exit 1
fi
executable="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$plist")"
if [[ ! -x "$built/Contents/MacOS/$executable" ]]; then
  echo "Xcode did not package an executable Praxis app." >&2
  exit 1
fi

staging="$(mktemp -d "$artifacts/.praxis-package.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
ditto "$built" "$staging/Praxis.app"
mv -n "$staging/Praxis.app" "$destination"
if [[ -e "$staging/Praxis.app" ]]; then
  echo "Another build created the destination before publication: $destination" >&2
  exit 1
fi

echo "Created unsigned development application: $destination"
echo "Launch with: open \"$destination\""
