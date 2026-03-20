#!/usr/bin/env bash
set -euo pipefail

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
TOOL_NAME=$(printf '%s' "$INPUT" | jq -r '.toolName // .tool_name // empty' | tr '[:upper:]' '[:lower:]')
TOOL_ARGS_OBJ=$(printf '%s' "$INPUT" | jq -rc '
  if (.toolArgs | type) == "string" then ((.toolArgs | fromjson?) // {})
  elif (.toolArgs | type) == "object" then .toolArgs
  elif (.tool_args | type) == "string" then ((.tool_args | fromjson?) // {})
  elif (.tool_args | type) == "object" then .tool_args
  else {}
  end')
CMD=$(printf '%s' "$TOOL_ARGS_OBJ" | jq -r '.command // .cmd // empty')
SESSION_ID=$(printf '%s' "$INPUT" | jq -r '
  .sessionId //
  .session_id //
  .sessionID //
  .conversationId //
  .conversation_id //
  .threadId //
  .thread_id //
  empty')
CWD=$(printf '%s' "$TOOL_ARGS_OBJ" | jq -r '
  .cwd //
  .workdir //
  .workingDirectory //
  .working_directory //
  empty')
if [[ -z "$CWD" ]]; then
  CWD=$(printf '%s' "$INPUT" | jq -r '.cwd // .workdir // .workingDirectory // .working_directory // empty')
fi
STATE_DIR="${HOME}/.copilot/hooks/.state"
CALLGRAPH_FALLBACK_AFTER_FAILURES="${COPILOT_CALLGRAPH_FALLBACK_AFTER_FAILURES:-2}"

if ! [[ "$CALLGRAPH_FALLBACK_AFTER_FAILURES" =~ ^[0-9]+$ ]]; then
  CALLGRAPH_FALLBACK_AFTER_FAILURES=2
fi

deny() {
  jq -nc --arg reason "$1" '{"permissionDecision":"deny","permissionDecisionReason":$reason}'
  exit 0
}

session_key() {
  if [[ -n "${SESSION_ID:-}" ]]; then
    printf '%s' "$SESSION_ID" | tr -cs 'A-Za-z0-9._-' '_'
    return
  fi

  if [[ -n "${CWD:-}" ]]; then
    printf '%s' "$CWD" | shasum -a 256 | awk '{print substr($1,1,20)}'
    return
  fi

  printf '%s' "global"
}

read_counter() {
  local file="$1"
  if [[ -f "$file" ]]; then
    local v
    v=$(cat "$file" 2>/dev/null || printf '%s' "0")
    if [[ "$v" =~ ^[0-9]+$ ]]; then
      printf '%s' "$v"
      return
    fi
  fi
  printf '%s' "0"
}

write_counter() {
  local file="$1"
  local value="$2"
  printf '%s' "$value" > "$file"
}

increment_counter() {
  local file="$1"
  local v
  v=$(read_counter "$file")
  v=$((v + 1))
  write_counter "$file" "$v"
  printf '%s' "$v"
}

callgraph_failure_counter_file() {
  local key
  key=$(session_key)
  printf '%s' "${STATE_DIR}/callgraph-failure-count-${key}.txt"
}

record_callgraph_failure() {
  mkdir -p "$STATE_DIR"
  local file
  file=$(callgraph_failure_counter_file)
  increment_counter "$file" >/dev/null
}

reset_callgraph_failures() {
  mkdir -p "$STATE_DIR"
  local file
  file=$(callgraph_failure_counter_file)
  write_counter "$file" "0"
}

current_callgraph_failures() {
  local file
  file=$(callgraph_failure_counter_file)
  read_counter "$file"
}

mark_main_callgraph_usage() {
  mkdir -p "$STATE_DIR"
  local key
  key=$(session_key)
  local file="${STATE_DIR}/callgraph-main-count-${key}.txt"
  local current
  current=$(read_counter "$file")
  printf '%s' "$((current + 1))" > "$file"
}

deny_with_callgraph_failure() {
  record_callgraph_failure
  deny "$1"
}

is_narrow_shell_fallback() {
  printf '%s' "$1" | grep -Eqi '^[[:space:]]*(rg|grep|find)\b' && \
    printf '%s' "$1" | grep -Eqi '(\|[[:space:]]*(head|tail)\b|--max-count\b|(^|[[:space:]])-m[[:space:]]+[0-9]+|sed[[:space:]]+-n)'
}

# Non-shell tools are out of scope for this policy.
if [[ "$TOOL_NAME" != "bash" && "$TOOL_NAME" != "powershell" ]]; then
  exit 0
fi

if [[ -z "$CMD" ]]; then
  exit 0
fi

# Allow test-targeted shell exploration because tests are excluded from CallGraph index scope.
if printf '%s' "$CMD" | grep -Eqi '\b(find|grep|rg|ls)\b' && \
   printf '%s' "$CMD" | grep -Eqi '((^|[/\\_.-])tests?([/\\_.-]|$)|\.tests?\.csproj\b|[._-]tests?\b|\b(xunit|nunit|mstest)\b)'; then
  exit 0
fi

# Guard against common callgraph usage errors.
if printf '%s' "$CMD" | grep -Eqi '\bcallgraph\b' && printf '%s' "$CMD" | grep -Eqi '\banalyze\b'; then
  if ! printf '%s' "$CMD" | grep -Eqi -- '--file(path|Path)([[:space:]]+|=)'; then
    deny_with_callgraph_failure 'callgraph analyze requires --filepath <absolute-file.cs>. Example: callgraph analyze --filepath /abs/path/Foo.cs --method Bar --direction outbound --visibility external --depth 2 2>&1'
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
    deny_with_callgraph_failure 'callgraph analyze with --visibility internal supports max --depth 2. Use two-stage analysis: inbound+external depth 2 first, then outbound+internal depth 2 on 1-3 selected methods.'
  fi
fi

# Guard against chained get-method-source calls.
if printf '%s' "$CMD" | grep -Eqi '\bcallgraph[[:space:]]+get-method-source\b'; then
  GET_METHOD_SOURCE_COUNT=$(printf '%s' "$CMD" | grep -Eo 'callgraph[[:space:]]+get-method-source' | wc -l | tr -d ' ')
  if [[ "${GET_METHOD_SOURCE_COUNT:-0}" -gt 1 ]] || printf '%s' "$CMD" | grep -Eq '&&|;'; then
    deny_with_callgraph_failure 'Chained callgraph get-method-source commands are not allowed. Run one get-method-source command per tool call, then summarize.'
  fi
fi

# Allow direct callgraph commands.
if printf '%s' "$CMD" | grep -Eq '^[[:space:]]*callgraph\b'; then
  mark_main_callgraph_usage
  reset_callgraph_failures
  exit 0
fi

# Enforce CallGraph-first for C# shell exploration patterns.
if printf '%s' "$CMD" | grep -Eqi '\b(find|grep|rg|ls)\b' && \
   printf '%s' "$CMD" | grep -Eqi '(\.cs([^[:alnum:]_]|$)|-name[[:space:]]+"?\*?\.cs|/src|xargs[[:space:]]+grep)'; then
  FAILURES=$(current_callgraph_failures)
  if [[ "$CALLGRAPH_FALLBACK_AFTER_FAILURES" -gt 0 ]] && [[ "$FAILURES" -ge "$CALLGRAPH_FALLBACK_AFTER_FAILURES" ]] && is_narrow_shell_fallback "$CMD"; then
    exit 0
  fi

  deny_with_callgraph_failure 'C# exploration should use CallGraph first. Try callgraph search-file, callgraph list-methods, or callgraph get-method-source.'
fi

# Allow all other commands.
exit 0
