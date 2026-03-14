#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

configure_claude_hook() {
  local claude_dir="$HOME/.claude"
  local hook_path="$claude_dir/hooks/callgraph-rewrite.sh"
  local settings_path="$claude_dir/settings.json"

  if [[ ! -f "$hook_path" ]]; then
    echo "Skipping Claude hook setup: hook file missing at $hook_path"
    return
  fi

  if ! command -v jq >/dev/null 2>&1; then
    echo "Skipping Claude hook setup: jq not available."
    return
  fi

  mkdir -p "$(dirname "$settings_path")"
  if [[ ! -f "$settings_path" ]] || [[ ! -s "$settings_path" ]]; then
    printf '{}\n' > "$settings_path"
  fi

  if ! jq -e 'type == "object"' "$settings_path" >/dev/null 2>&1; then
    echo "Skipping Claude hook setup: $settings_path is not a JSON object."
    return
  fi

  local tmp_file
  tmp_file="$(mktemp)"
  jq --arg hook "$hook_path" '
    if (.hooks | type) != "object" then .hooks = {} else . end
    | if (.hooks.PreToolUse | type) != "array" then .hooks.PreToolUse = [] else . end
    | if any(.hooks.PreToolUse[]?;
        .matcher == "Bash"
        and ((.hooks | type) == "array")
        and any(.hooks[]?; .type == "command" and .command == $hook))
      then .
      else .hooks.PreToolUse += [{"matcher":"Bash","hooks":[{"type":"command","command":$hook}]}]
      end
  ' "$settings_path" > "$tmp_file"
  mv "$tmp_file" "$settings_path"
  echo "Configured Claude PreToolUse hook in $settings_path"
}

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

copy_content "$SCRIPT_DIR/_claude" "$HOME/.claude"
copy_content "$SCRIPT_DIR/_codex" "$HOME/.codex"
copy_content "$SCRIPT_DIR/_cursor" "$HOME/.cursor"
configure_claude_hook

echo "Done."
