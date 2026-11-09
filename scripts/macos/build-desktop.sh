#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
destination="$root/.artifacts/Praxis.app"
if [[ -e "$destination" ]]; then
  echo "Destination already exists: $destination" >&2
  echo "Remove only the previous development app bundle before rebuilding." >&2
  exit 1
fi

mkdir -p "$destination/Contents/MacOS" "$destination/Contents/Resources/worker"
dotnet publish "$root/src/Praxis.Worker/Praxis.Worker.csproj" \
  --configuration Release --output "$destination/Contents/Resources/worker" \
  --no-restore

swiftc -parse-as-library -O \
  "$root/apps/macos/PraxisApp.swift" \
  -framework SwiftUI -framework AppKit \
  -o "$destination/Contents/MacOS/Praxis"

cat > "$destination/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
 "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>Praxis</string>
  <key>CFBundleIdentifier</key><string>dev.praxis.local</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>Praxis</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

echo "Created unsigned local-development app: $destination"
echo "Launch with: open \"$destination\""
