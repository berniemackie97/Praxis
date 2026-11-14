#!/usr/bin/env bash
# Builds the praxis-vision OCR and transcription helper into the app's Resources for every target architecture.
set -euo pipefail

: "${SRCROOT:?The build must run from the Praxis Xcode app target.}"
: "${TARGET_BUILD_DIR:?Missing Xcode product directory.}"
: "${UNLOCALIZED_RESOURCES_FOLDER_PATH:?Missing application resources path.}"
: "${DERIVED_FILE_DIR:?Missing Xcode derived files directory.}"

source="$SRCROOT/Helpers/PraxisVision/main.swift"
output="$TARGET_BUILD_DIR/$UNLOCALIZED_RESOURCES_FOLDER_PATH/praxis-vision"
deployment="${MACOSX_DEPLOYMENT_TARGET:-13.0}"
work="$DERIVED_FILE_DIR/praxis-vision"
mkdir -p "$work" "$(dirname "$output")"

if [[ -x "$output" && "$output" -nt "$source" ]]; then
  exit 0
fi

slices=()
for arch in ${ARCHS:-$(uname -m)}; do
  xcrun --sdk macosx swiftc -O -whole-module-optimization -target "$arch-apple-macos$deployment" \
    -framework Vision -framework PDFKit -framework Speech -framework AVFoundation -o "$work/praxis-vision-$arch" "$source"
  slices+=("$work/praxis-vision-$arch")
done
xcrun lipo -create "${slices[@]}" -output "$output"
chmod 755 "$output"
