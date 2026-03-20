#!/usr/bin/env bash
set -euo pipefail

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
TOOL_NAME=$(printf '%s' "$INPUT" | jq -r '.toolName // empty')
TOOL_ARGS_OBJ=$(printf '%s' "$INPUT" | jq -rc 'if (.toolArgs | type) == "string" then ((.toolArgs | fromjson?) // {}) elif (.toolArgs | type) == "object" then .toolArgs else {} end')
CMD=$(printf '%s' "$TOOL_ARGS_OBJ" | jq -r '.command // empty')

deny() {
  jq -nc --arg reason "$1" '{"permissionDecision":"deny","permissionDecisionReason":$reason}'
  exit 0
}

# Non-shell tools are out of scope for this policy.
if [[ "$TOOL_NAME" != "bash" && "$TOOL_NAME" != "powershell" ]]; then
  exit 0
fi

if [[ -z "$CMD" ]]; then
  exit 0
fi

# Guard against common callgraph usage errors.
if printf '%s' "$CMD" | grep -Eqi '\bcallgraph\b' && printf '%s' "$CMD" | grep -Eqi '\banalyze\b'; then
  if printf '%s' "$CMD" | grep -Eqi '\banalyze-callgraph\b'; then
    deny 'Unknown command analyze-callgraph. Use: callgraph analyze --filepath <absolute-file.cs> [--method <name>] [--direction inbound|outbound|bi-directional] [--visibility external|internal] [--depth <n>] 2>&1'
  fi

  if ! printf '%s' "$CMD" | grep -Eq -- '--filepath([[:space:]]+|=)'; then
    deny 'callgraph analyze requires --filepath <absolute-file.cs>. Example: callgraph analyze --filepath /abs/path/Foo.cs --method Bar --direction outbound --visibility external --depth 2 2>&1'
  fi

  VISIBILITY=$(printf '%s' "$CMD" | sed -nE 's/.*--visibility[[:space:]]+([^[:space:]]+).*/\1/p' | head -n1)
  if [[ -z "$VISIBILITY" ]]; then
    VISIBILITY=$(printf '%s' "$CMD" | sed -nE 's/.*--visibility=([^[:space:]]+).*/\1/p' | head -n1)
  fi

  DEPTH=$(printf '%s' "$CMD" | sed -nE 's/.*--depth[[:space:]]+([0-9]+).*/\1/p' | head -n1)
  if [[ -z "$DEPTH" ]]; then
    DEPTH=$(printf '%s' "$CMD" | sed -nE 's/.*--depth=([0-9]+).*/\1/p' | head -n1)
  fi
  if [[ -z "$DEPTH" ]]; then
    DEPTH=1
  fi

  if printf '%s' "$VISIBILITY" | grep -Eqi '^internal$' && [[ "$DEPTH" -gt 2 ]]; then
    deny 'callgraph analyze with --visibility internal supports max --depth 2. Use two-stage analysis: inbound+external depth 2 first, then outbound+internal depth 2 on 1-3 selected methods.'
  fi
fi

# Guard against chained get-method-source calls.
if printf '%s' "$CMD" | grep -Eqi '\bcallgraph[[:space:]]+get-method-source\b'; then
  GET_METHOD_SOURCE_COUNT=$(printf '%s' "$CMD" | grep -Eo 'callgraph[[:space:]]+get-method-source' | wc -l | tr -d ' ')
  if [[ "${GET_METHOD_SOURCE_COUNT:-0}" -gt 1 ]] || printf '%s' "$CMD" | grep -Eq '&&|;'; then
    deny 'Chained callgraph get-method-source commands are not allowed. Run one get-method-source command per tool call, then summarize.'
  fi
fi

# Guard against relative --filePath for file-scoped commands.
if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*callgraph[[:space:]]+(list-methods|get-method-source|search-file|search-method)\b' && \
   printf '%s' "$CMD" | grep -Eq -- '--filePath([[:space:]]+|=)'; then
  FILE_PATH_ARG=$(printf '%s' "$CMD" | sed -nE 's/.*--filePath[[:space:]]+([^[:space:]]+).*/\1/p' | head -n1)
  if [[ -z "$FILE_PATH_ARG" ]]; then
    FILE_PATH_ARG=$(printf '%s' "$CMD" | sed -nE 's/.*--filePath=([^[:space:]]+).*/\1/p' | head -n1)
  fi

  FILE_PATH_ARG=$(printf '%s' "$FILE_PATH_ARG" | sed -E 's/^"//; s/"$//; s/^'\''//; s/'\''$//')
  if [[ -n "$FILE_PATH_ARG" ]] && ! printf '%s' "$FILE_PATH_ARG" | grep -Eq '^/'; then
    deny 'callgraph --filePath must be absolute. Use an absolute .cs path, or use --folderPath for scoped discovery first.'
  fi
fi

# Allow direct callgraph commands.
if printf '%s' "$CMD" | grep -Eq '^[[:space:]]*callgraph\b'; then
  exit 0
fi

# Enforce CallGraph-first for C# shell exploration patterns.
if printf '%s' "$CMD" | grep -Eqi '\b(find|grep|rg|ls)\b' && \
   printf '%s' "$CMD" | grep -Eqi '(\.cs([^[:alnum:]_]|$)|-name[[:space:]]+"?\*?\.cs|/src|xargs[[:space:]]+grep)'; then
  deny 'C# exploration should use CallGraph first. Try callgraph search-file, callgraph list-methods, or callgraph get-method-source. Use --includeTests false when you need to exclude test-project results.'
fi

# Allow all other commands.
exit 0
