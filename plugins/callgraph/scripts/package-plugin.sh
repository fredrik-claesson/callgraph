#!/usr/bin/env bash
#
# Produces a self-contained, redistributable marketplace archive of the
# CallGraph plugin (binaries included) under plugins/callgraph/dist/.
#
# The archive unpacks to a directory that IS a Claude Code marketplace:
#
#   callgraph-marketplace/
#   ├── .claude-plugin/marketplace.json   (source: ./callgraph)
#   └── callgraph/                         (the plugin: manifest, skills, launchers, bin/<rid>/)
#
# The recipient then runs:
#   /plugin marketplace add /abs/path/to/callgraph-marketplace
#   /plugin install callgraph@callgraph-marketplace
#
# Binaries are gitignored, so this packaging step is how you hand the plugin to
# someone with the executables included. Build them first (or pass RIDs to build
# the missing ones):
#   plugins/callgraph/scripts/build-binaries.sh
#
# Usage:
#   plugins/callgraph/scripts/package-plugin.sh                 # package whatever bin/<rid>/ exist
#   plugins/callgraph/scripts/package-plugin.sh osx-arm64 ...   # build these RIDs first, then package
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
# Output lives OUTSIDE the plugin dir (plugins/dist), so a local-path marketplace
# install does not copy the packaged archive into the installed plugin cache.
DIST_DIR="$(cd "$PLUGIN_DIR/.." && pwd)/dist"
VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PLUGIN_DIR/.claude-plugin/plugin.json" | head -1)"
VERSION="${VERSION:-0.0.0}"

# If RIDs were requested, (re)build them first.
if [[ $# -gt 0 ]]; then
  "$SCRIPT_DIR/build-binaries.sh" "$@"
fi

# Require at least one built runtime.
shopt -s nullglob
built=("$PLUGIN_DIR"/bin/*/)
shopt -u nullglob
if [[ ${#built[@]} -eq 0 ]]; then
  echo "No binaries found in $PLUGIN_DIR/bin/<rid>/." >&2
  echo "Build them first: $SCRIPT_DIR/build-binaries.sh [rid...]" >&2
  exit 1
fi

STAGE="$DIST_DIR/callgraph-marketplace"
rm -rf "$STAGE"
mkdir -p "$STAGE/.claude-plugin" "$STAGE/callgraph"

# Copy the plugin, excluding debug symbols and OS cruft.
tar -C "$PLUGIN_DIR" \
  --exclude "*.pdb" \
  --exclude ".DS_Store" \
  -cf - . | tar -C "$STAGE/callgraph" -xf -

# Emit a marketplace manifest whose plugin source points at the sibling plugin dir.
cat > "$STAGE/.claude-plugin/marketplace.json" <<'JSON'
{
  "name": "callgraph-marketplace",
  "owner": {
    "name": "Fredrik Claesson",
    "email": "fredrik.claesson@mews.com"
  },
  "plugins": [
    {
      "name": "callgraph",
      "source": "./callgraph",
      "description": "CallGraph: Roslyn-indexed C# call-graph analysis over a local SQLite index, with a bundled CLI binary and two skills.",
      "keywords": ["csharp", "roslyn", "call-graph", "code-navigation", "sqlite"]
    }
  ]
}
JSON

archive="$DIST_DIR/callgraph-marketplace-$VERSION.tar.gz"
tar -C "$DIST_DIR" -czf "$archive" "callgraph-marketplace"

echo "Included runtimes:"
for d in "$STAGE"/callgraph/bin/*/; do echo "  - $(basename "$d")"; done
echo "Staged marketplace: $STAGE"
echo "Packaged archive:   $archive"
