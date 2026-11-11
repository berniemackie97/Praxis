#!/usr/bin/env bash
set -euo pipefail

: "${SRCROOT:?The build must run from the Praxis Xcode app target.}"
: "${TARGET_BUILD_DIR:?Missing Xcode product directory.}"
: "${UNLOCALIZED_RESOURCES_FOLDER_PATH:?Missing application resources path.}"
: "${DERIVED_FILE_DIR:?Missing Xcode derived files directory.}"

case "${CONFIGURATION:-}" in
  Debug|Release) ;;
  *) echo "Unsupported Praxis worker build configuration: ${CONFIGURATION:-unset}" >&2; exit 1 ;;
esac

root="$(cd "$SRCROOT/../.." && pwd)"
worker_project="$root/src/Praxis.Worker/Praxis.Worker.csproj"

# Xcode launched from Finder doesn't necessarily inherit the interactive shell PATH.
dotnet=""
candidates=(
  "${PRAXIS_DOTNET_EXECUTABLE:-}"
  "${DOTNET_ROOT:+$DOTNET_ROOT/dotnet}"
  "$HOME/.dotnet/dotnet"
  "/usr/local/share/dotnet/dotnet"
  "/opt/homebrew/bin/dotnet"
  "/opt/homebrew/opt/dotnet/libexec/dotnet"
  "/usr/local/bin/dotnet"
)
for candidate in "${candidates[@]}"; do
  if [[ -n "$candidate" && -x "$candidate" ]]; then
    dotnet="$candidate"
    break
  fi
done
if [[ -z "$dotnet" ]] && command -v dotnet >/dev/null 2>&1; then
  dotnet="$(command -v dotnet)"
fi
if [[ -z "$dotnet" ]]; then
  echo "The .NET 10 SDK was not found by Xcode." >&2
  echo "Set the user-defined Xcode build setting PRAXIS_DOTNET_EXECUTABLE to the absolute dotnet executable path." >&2
  exit 1
fi

for assembly in Praxis.Domain Praxis.Application Praxis.Infrastructure Praxis.Worker; do
  project="$root/src/$assembly/$assembly.csproj"
  target_file="$("$dotnet" msbuild "$project" -getProperty:TargetFileName -nologo)"
  if [[ "$target_file" != "$assembly.dll" ]]; then
    echo "The Xcode environment changed the .NET output identity for $assembly: $target_file" >&2
    exit 1
  fi
done

mkdir -p "$DERIVED_FILE_DIR"
staging="$(mktemp -d "$DERIVED_FILE_DIR/praxis-worker.XXXXXX")"
trap 'rm -rf "$staging"' EXIT

"$dotnet" restore "$worker_project" --locked-mode
"$dotnet" publish "$worker_project" \
  --configuration "$CONFIGURATION" --no-restore --output "$staging"

for required in Praxis.Worker.dll Praxis.Infrastructure.dll Praxis.Application.dll Praxis.Domain.dll Praxis.Worker.deps.json Praxis.Worker.runtimeconfig.json; do
  if [[ ! -f "$staging/$required" ]]; then
    echo "The .NET publish output is missing $required." >&2
    exit 1
  fi
done

destination="$TARGET_BUILD_DIR/$UNLOCALIZED_RESOURCES_FOLDER_PATH/worker"
mkdir -p "$destination"
rsync -a --delete "$staging/" "$destination/"
echo "Published the .NET worker into the Xcode-built Praxis.app bundle."
