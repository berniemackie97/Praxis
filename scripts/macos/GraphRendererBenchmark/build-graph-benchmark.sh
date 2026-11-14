#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/../../.." && pwd)"
source "$root/scripts/macos/xcode-toolchain.sh"

source_dir="$root/scripts/macos/GraphRendererBenchmark"
artifacts="$root/.artifacts"
bundle="$artifacts/GraphRendererBenchmark.app"
mkdir -p "$artifacts"

stage="$(mktemp -d "$artifacts/.graph-benchmark.XXXXXX")"
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/Contents/MacOS"

cat > "$stage/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>GraphRendererBenchmark</string>
    <key>CFBundleIdentifier</key>
    <string>dev.praxis.graph-renderer-benchmark</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>Praxis Graph Renderer Benchmark</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

xcrun swiftc -O -swift-version 6 -parse-as-library \
  -target "$(uname -m)-apple-macos13.0" \
  "$source_dir"/*.swift \
  -o "$stage/Contents/MacOS/GraphRendererBenchmark"

plutil -lint "$stage/Contents/Info.plist"
codesign --force --sign - "$stage"

# Only replace the generated benchmark bundle under .artifacts.
if [[ -e "$bundle" ]]; then
  if [[ ! -f "$bundle/Contents/Info.plist" ]] ||
     [[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$bundle/Contents/Info.plist" 2>/dev/null)" != "dev.praxis.graph-renderer-benchmark" ]]; then
    echo "Refusing to replace a bundle that is not the generated benchmark: $bundle" >&2
    exit 1
  fi
  rm -rf "$bundle"
fi

mv "$stage" "$bundle"
printf 'Built %s\n' "$bundle"
printf 'Launch: open -n -a "%s" --args /path/to/synthetic-case.json\n' "$bundle"
