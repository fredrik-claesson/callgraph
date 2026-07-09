#!/usr/bin/env bash
#
# Produces a redistributable archive of the CallGraph plugin with binaries
# included. Builds the requested runtimes (default: all), then tars the plugin
# directory (skills + manifest + launchers + bin/<rid>/ binaries) into
# plugins/callgraph/dist/.
#
# Because the binaries are gitignored, this is the intended way to hand the
# plugin to someone: build + package, then share the archive (or point a local
# marketplace at the unpacked directory).
#
# Usage:
#   plugins/callgraph/scripts/package-plugin.sh                 # all RIDs
#   plugins/callgraph/scripts/package-plugin.sh osx-arm64 linux-x64
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
DIST_DIR="$PLUGIN_DIR/dist"
VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PLUGIN_DIR/.claude-plugin/plugin.json" | head -1)"
VERSION="${VERSION:-0.0.0}"

"$SCRIPT_DIR/build-binaries.sh" "$@"

mkdir -p "$DIST_DIR"
archive="$DIST_DIR/callgraph-plugin-$VERSION.tar.gz"

# Archive the plugin as a top-level "callgraph/" directory, excluding the dist
# folder itself and any stray OS metadata.
tar -czf "$archive" \
  -C "$(dirname "$PLUGIN_DIR")" \
  --exclude "callgraph/dist" \
  --exclude ".DS_Store" \
  "callgraph"

echo "Packaged: $archive"
