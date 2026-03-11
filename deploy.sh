#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

copy_content() {
  local source_dir="$1"
  local target_dir="$2"

  if [[ ! -d "$source_dir" ]]; then
    echo "Skipping missing directory: $source_dir"
    return
  fi

  mkdir -p "$target_dir"
  cp -R "$source_dir"/. "$target_dir"/
  echo "Deployed: $source_dir -> $target_dir"
}

copy_content "$SCRIPT_DIR/_claude" "$HOME/.claude"
copy_content "$SCRIPT_DIR/_codex" "$HOME/.codex"

echo "Done."
