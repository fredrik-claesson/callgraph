#!/usr/bin/env bash
# callgraph hook: rewrites shell C# search commands to callgraph equivalents.
# Requires: callgraph, jq

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

if ! command -v callgraph >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
CMD=$(echo "$INPUT" | jq -r '.tool_input.command // empty')

if [ -z "$CMD" ]; then
  exit 0
fi

REWRITTEN=$(callgraph rewrite --command "$CMD" 2>/dev/null) || exit 0

if [ -z "$REWRITTEN" ] || [ "$CMD" = "$REWRITTEN" ]; then
  exit 0
fi

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
