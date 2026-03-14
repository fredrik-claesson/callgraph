#!/usr/bin/env bash
# callgraph hook: rewrites shell C# search commands to callgraph equivalents.
# Requires: callgraph, jq

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
CMD=$(echo "$INPUT" | jq -r '.tool_input.command // empty')

if [ -z "$CMD" ]; then
  exit 0
fi

# Only govern shell search commands that look like C# codebase exploration.
if ! printf '%s' "$CMD" | grep -Eqi '\b(find|grep|rg)\b'; then
  exit 0
fi

if ! printf '%s' "$CMD" | grep -Eqi '(\.cs([^[:alnum:]_]|$)|-name[[:space:]]+"?\*?\.cs|/src|/Api/Commander|Mews\.Server\.Web|xargs[[:space:]]+grep)'; then
  exit 0
fi

if ! command -v callgraph >/dev/null 2>&1; then
  jq -n \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": "C# code search via find/grep/rg is blocked. Use CallGraph skills/tools instead."
      }
    }'
  exit 0
fi

REWRITTEN=$(callgraph rewrite --command "$CMD" 2>/dev/null) || REWRITTEN=""

if [ -n "$REWRITTEN" ] && [ "$CMD" != "$REWRITTEN" ]; then
  ORIGINAL_INPUT=$(echo "$INPUT" | jq -c '.tool_input')
  UPDATED_INPUT=$(echo "$ORIGINAL_INPUT" | jq --arg cmd "$REWRITTEN" '.command = $cmd')

  jq -n \
    --argjson updated "$UPDATED_INPUT" \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "allow",
        "permissionDecisionReason": "CallGraph auto-rewrite",
        "updatedInput": $updated
      }
    }'
  exit 0
fi

jq -n \
  '{
    "hookSpecificOutput": {
      "hookEventName": "PreToolUse",
      "permissionDecision": "deny",
      "permissionDecisionReason": "C# code search via find/grep/rg is blocked. Use CallGraph skills/tools instead."
    }
  }'
