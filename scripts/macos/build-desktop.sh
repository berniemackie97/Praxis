#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
source "$root/scripts/macos/xcode-toolchain.sh"
artifacts="$root/.artifacts"
destination="$artifacts/Praxis.app"
minimum_macos=13.0
if [[ -e "$destination" || -L "$destination" ]]; then
  echo "Destination already exists: $destination" >&2
  echo "Remove only the previous development app bundle before rebuilding." >&2
  exit 1
fi

mkdir -p "$artifacts"
staging="$(mktemp -d "$artifacts/.praxis-build.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
bundle="$staging/Praxis.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources/worker"
dotnet publish "$root/src/Praxis.Worker/Praxis.Worker.csproj" \
  --configuration Release --output "$bundle/Contents/Resources/worker" \
  --no-restore

swift build --package-path "$PRAXIS_SWIFT_PACKAGE" --configuration release --product Praxis
cp "$(swift build --package-path "$PRAXIS_SWIFT_PACKAGE" --configuration release --show-bin-path)/Praxis" \
  "$bundle/Contents/MacOS/Praxis"

cat > "$bundle/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
 "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>Praxis</string>
  <key>CFBundleIdentifier</key><string>dev.praxis.renames-preview</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>Praxis</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>${minimum_macos}</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

plutil -lint "$bundle/Contents/Info.plist"
mv -n "$bundle" "$artifacts/"
if [[ -e "$bundle" ]]; then
  echo "Destination already exists: $destination" >&2
  exit 1
fi

echo "Created unsigned local-development app: $destination"
echo "Launch with: open \"$destination\""
