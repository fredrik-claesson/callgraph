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

if [[ -x "$SCRIPT_DIR/scripts/clean-distributables.sh" ]]; then
  "$SCRIPT_DIR/scripts/clean-distributables.sh"
fi

# Install only the bundled skills into the user's Claude config.
# We intentionally do NOT copy _claude/CLAUDE.md, so an existing
# ~/.claude/CLAUDE.md (the user's global instructions) is never overwritten.
copy_content "$SCRIPT_DIR/_claude/skills" "$HOME/.claude/skills"

echo "Done. Installed CallGraph skills to $HOME/.claude/skills."
