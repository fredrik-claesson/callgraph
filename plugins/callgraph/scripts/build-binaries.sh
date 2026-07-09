#!/usr/bin/env bash
#
# Publishes the CallGraph CLI as self-contained single-file executables for all
# supported runtimes into plugins/callgraph/bin/<rid>/, so the plugin can be
# zipped/redistributed with binaries included.
#
# The binaries are intentionally gitignored (they are large and rebuilt often);
# run this before packaging the plugin for distribution.
#
# Usage:
#   plugins/callgraph/scripts/build-binaries.sh            # all RIDs
#   plugins/callgraph/scripts/build-binaries.sh osx-arm64  # a subset
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$PLUGIN_DIR/../.." && pwd)"
CSPROJ="$REPO_ROOT/CallGraph.csproj"
BIN_DIR="$PLUGIN_DIR/bin"

ALL_RIDS=(osx-arm64 osx-x64 linux-x64 win-x64)
RIDS=("$@")
if [[ ${#RIDS[@]} -eq 0 ]]; then
  RIDS=("${ALL_RIDS[@]}")
fi

for rid in "${RIDS[@]}"; do
  out="$BIN_DIR/$rid"
  echo "==> Publishing $rid -> $out"
  rm -rf "$out"
  dotnet publish "$CSPROJ" -c Release -r "$rid" --self-contained true -o "$out" -v q
done

echo "Done. Binaries in $BIN_DIR/<rid>/."
