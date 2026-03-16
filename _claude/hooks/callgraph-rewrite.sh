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

allow_command() {
  jq -n \
    --arg reason "$1" \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "allow",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
}

# Allow lightweight environment checks so agents can diagnose CallGraph availability.
if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*(which|command[[:space:]]+-v|type[[:space:]]+-P)[[:space:]]+callgraph\b'; then
  allow_command "Allowed: CallGraph availability check"
fi

if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*ls\b' && printf '%s' "$CMD" | grep -Eqi '(\.local/bin|\.dotnet/tools|/tools/?$|/tools/|callgraph)'; then
  allow_command "Allowed: CallGraph install/path inspection"
fi

# Guard against explosive internal callgraph traversals.
if printf '%s' "$CMD" | grep -Eqi '\bcallgraph\b' && printf '%s' "$CMD" | grep -Eqi '\banalyze\b'; then
  # Common typo guard: analyze-callgraph is not a valid command.
  if printf '%s' "$CMD" | grep -Eqi '\banalyze-callgraph\b'; then
    jq -n \
      '{
        "hookSpecificOutput": {
          "hookEventName": "PreToolUse",
          "permissionDecision": "deny",
          "permissionDecisionReason": "Unknown command analyze-callgraph. Use: callgraph analyze --filepath <absolute-file.cs> [--method <name>] [--direction inbound|outbound|bi-directional] [--visibility external|internal] [--depth <n>] 2>&1"
        }
      }'
    exit 0
  fi

  # analyze requires --filepath; provide corrective guidance early.
  if ! printf '%s' "$CMD" | grep -Eq -- '--filepath([[:space:]]+|=)'; then
    jq -n \
      '{
        "hookSpecificOutput": {
          "hookEventName": "PreToolUse",
          "permissionDecision": "deny",
          "permissionDecisionReason": "callgraph analyze requires --filepath <absolute-file.cs>. Example: callgraph analyze --filepath /abs/path/Foo.cs --method Bar --direction outbound --visibility external --depth 2 2>&1"
        }
      }'
    exit 0
  fi

  VISIBILITY=$(printf '%s' "$CMD" | sed -nE 's/.*--visibility[[:space:]]+([^[:space:]]+).*/\1/p' | head -n1)
  if [ -z "$VISIBILITY" ]; then
    VISIBILITY=$(printf '%s' "$CMD" | sed -nE 's/.*--visibility=([^[:space:]]+).*/\1/p' | head -n1)
  fi

  DEPTH=$(printf '%s' "$CMD" | sed -nE 's/.*--depth[[:space:]]+([0-9]+).*/\1/p' | head -n1)
  if [ -z "$DEPTH" ]; then
    DEPTH=$(printf '%s' "$CMD" | sed -nE 's/.*--depth=([0-9]+).*/\1/p' | head -n1)
  fi
  if [ -z "$DEPTH" ]; then
    DEPTH=1
  fi

  if printf '%s' "$VISIBILITY" | grep -Eqi '^internal$' && [ "$DEPTH" -gt 2 ]; then
    jq -n \
      '{
        "hookSpecificOutput": {
          "hookEventName": "PreToolUse",
          "permissionDecision": "deny",
          "permissionDecisionReason": "Blocked: callgraph analyze with --visibility internal supports max --depth 2. Use two-stage analysis: (1) map callers with inbound+external depth 2, (2) pick 1-3 candidates and run outbound+internal depth 2 per candidate."
        }
      }'
    exit 0
  fi
fi

# Guard against large chained method-source extraction scripts.
if printf '%s' "$CMD" | grep -Eqi '\bcallgraph[[:space:]]+get-method-source\b'; then
  GET_METHOD_SOURCE_COUNT=$(printf '%s' "$CMD" | grep -Eo 'callgraph[[:space:]]+get-method-source' | wc -l | tr -d ' ')
  if [ "${GET_METHOD_SOURCE_COUNT:-0}" -gt 1 ] || printf '%s' "$CMD" | grep -Eq '&&|;'; then
    jq -n \
      '{
        "hookSpecificOutput": {
          "hookEventName": "PreToolUse",
          "permissionDecision": "deny",
          "permissionDecisionReason": "Blocked: chained callgraph get-method-source commands are not allowed. Run one get-method-source call per command, then summarize; for multi-file inventory use callgraph list-methods --folderPath/--fileList first."
        }
      }'
    exit 0
  fi
fi

# Rewrite a common malformed search-method invocation:
#   callgraph search-method FooBar  -> callgraph search-method --pattern "*FooBar*"
if printf '%s' "$CMD" | grep -Eq '^[[:space:]]*callgraph[[:space:]]+search-method\b' && \
   ! printf '%s' "$CMD" | grep -Eq -- '--(pattern|keywords|regex)\b'; then
  BARE_QUERY=$(printf '%s' "$CMD" | sed -nE 's/^[[:space:]]*callgraph[[:space:]]+search-method[[:space:]]+("?[^-][^"]*"?)[[:space:]]*$/\1/p' | head -n1)
  if [ -n "$BARE_QUERY" ]; then
    BARE_QUERY=$(printf '%s' "$BARE_QUERY" | sed -E 's/^"//; s/"$//; s/^'\''//; s/'\''$//')
    if [ -n "$BARE_QUERY" ]; then
      REWRITTEN_CMD=$(printf 'callgraph search-method --pattern "*%s*" 2>&1' "$BARE_QUERY")
      ORIGINAL_INPUT=$(echo "$INPUT" | jq -c '.tool_input')
      UPDATED_INPUT=$(echo "$ORIGINAL_INPUT" | jq --arg cmd "$REWRITTEN_CMD" '.command = $cmd')
      jq -n \
        --argjson updated "$UPDATED_INPUT" \
        '{
          "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "allow",
            "permissionDecisionReason": "CallGraph auto-rewrite: added --pattern for search-method",
            "updatedInput": $updated
          }
        }'
      exit 0
    fi
  fi
fi

# Guard against relative --filePath on file-scoped callgraph commands.
if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*callgraph[[:space:]]+(list-methods|get-method-source|search-file|search-method)\b' && \
   printf '%s' "$CMD" | grep -Eq -- '--filePath([[:space:]]+|=)'; then
  FILE_PATH_ARG=$(printf '%s' "$CMD" | sed -nE 's/.*--filePath[[:space:]]+([^[:space:]]+).*/\1/p' | head -n1)
  if [ -z "$FILE_PATH_ARG" ]; then
    FILE_PATH_ARG=$(printf '%s' "$CMD" | sed -nE 's/.*--filePath=([^[:space:]]+).*/\1/p' | head -n1)
  fi
  FILE_PATH_ARG=$(printf '%s' "$FILE_PATH_ARG" | sed -E 's/^"//; s/"$//; s/^'\''//; s/'\''$//')
  if [ -n "$FILE_PATH_ARG" ] && ! printf '%s' "$FILE_PATH_ARG" | grep -Eq '^/'; then
    jq -n \
      '{
        "hookSpecificOutput": {
          "hookEventName": "PreToolUse",
          "permissionDecision": "deny",
          "permissionDecisionReason": "callgraph --filePath must be absolute. Use an absolute .cs path, or use --folderPath for scoped discovery first."
        }
      }'
    exit 0
  fi
fi

# Allow callgraph commands (including output filtering like `callgraph ... | grep ...`).
if printf '%s' "$CMD" | grep -Eq '^[[:space:]]*callgraph\b'; then
  exit 0
fi

# Only govern `ls` when there is explicit C# file intent.
if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*ls\b' && ! printf '%s' "$CMD" | grep -Eqi '\.cs([^[:alnum:]_]|$)|\*\.cs'; then
  exit 0
fi

# Only govern shell exploration commands that look like C# codebase exploration.
if ! printf '%s' "$CMD" | grep -Eqi '\b(find|grep|rg|ls)\b'; then
  exit 0
fi

if ! printf '%s' "$CMD" | grep -Eqi '(\.cs([^[:alnum:]_]|$)|-name[[:space:]]+"?\*?\.cs|/src|/Api/Commander|Mews\.Server\.Web|xargs[[:space:]]+grep)'; then
  exit 0
fi

if ! command -v callgraph >/dev/null 2>&1; then
  allow_command "Allowed: CallGraph unavailable, permitting narrow shell fallback"
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
      "permissionDecisionReason": "C# code exploration should use CallGraph first. If this exact query cannot be rewritten, retry with search-file/list-methods/get-method-source, or use one narrow shell fallback only when CallGraph is unavailable."
    }
  }'
