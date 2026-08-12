#!/usr/bin/env bash
#
# Build binaries for all supported platforms and publish a GitHub release with
# them as downloadable assets, so the callgraph-setup skill can install them
# without needing the binaries committed to git.
#
# Usage:
#   plugins/callgraph/scripts/release.sh <tag>            # e.g. v1.0.1
#   plugins/callgraph/scripts/release.sh <tag> --draft    # create a draft release
#
# Prerequisites:
#   - gh CLI authenticated (gh auth status)
#   - dotnet SDK (for building)
#   - The git working tree must be clean (release tags are pushed to origin)
#
# What it does:
#   1. Builds self-contained binaries for all RIDs via build-binaries.sh
#   2. Packages each RID into a tar.gz / zip
#   3. Creates (or updates) the GitHub release tag and uploads the assets
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BIN_DIR="$PLUGIN_DIR/bin"
REPO="fredrik-claesson/callgraph"

TAG="${1:-}"
DRAFT_FLAG=""
if [[ "${2:-}" == "--draft" ]]; then
  DRAFT_FLAG="--draft"
fi

if [[ -z "$TAG" ]]; then
  echo "Usage: $0 <tag> [--draft]" >&2
  echo "  e.g. $0 v1.0.1" >&2
  exit 1
fi

# Sanity-check prerequisites.
if ! command -v gh &>/dev/null; then
  echo "error: gh CLI is required (brew install gh)" >&2
  exit 1
fi
if ! gh auth status &>/dev/null; then
  echo "error: not authenticated with gh. Run: gh auth login" >&2
  exit 1
fi

echo "==> Building all RID binaries..."
"$SCRIPT_DIR/build-binaries.sh"

# Stage per-RID archives.
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

ASSETS=()

for rid_dir in "$BIN_DIR"/osx-arm64 "$BIN_DIR"/osx-x64 "$BIN_DIR"/linux-x64; do
  rid="$(basename "$rid_dir")"
  if [[ ! -d "$rid_dir" ]]; then continue; fi
  archive="$STAGE/callgraph-$rid.tar.gz"
  tar -C "$rid_dir" -czf "$archive" .
  ASSETS+=("$archive")
  echo "  packaged $rid -> $archive"
done

win_dir="$BIN_DIR/win-x64"
if [[ -d "$win_dir" ]]; then
  zip_file="$STAGE/callgraph-win-x64.zip"
  (cd "$win_dir" && zip -qr "$zip_file" .)
  ASSETS+=("$zip_file")
  echo "  packaged win-x64 -> $zip_file"
fi

echo "==> Creating GitHub release $TAG on $REPO..."
gh release create "$TAG" "${ASSETS[@]}" \
  --repo "$REPO" \
  --title "CallGraph $TAG" \
  --notes "Automated release of CallGraph $TAG. Install via the \`callgraph-setup\` Claude Code skill." \
  $DRAFT_FLAG

echo ""
echo "Release $TAG published: https://github.com/$REPO/releases/tag/$TAG"
echo "Assets:"
for asset in "${ASSETS[@]}"; do echo "  - $(basename "$asset")"; done
