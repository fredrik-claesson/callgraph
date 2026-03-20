#!/usr/bin/env bash
# callgraph hook: rewrites shell C# search commands to callgraph equivalents.
# Requires: callgraph, jq

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
CMD=$(echo "$INPUT" | jq -r '
  .tool_input.command //
  .tool_input.cmd //
  .toolInput.command //
  .toolInput.cmd //
  .command //
  .cmd //
  empty')
SESSION_ID=$(echo "$INPUT" | jq -r '
  .session_id //
  .sessionId //
  .sessionID //
  .conversation_id //
  .conversationId //
  .thread_id //
  .threadId //
  empty')
CWD=$(echo "$INPUT" | jq -r '
  .cwd //
  .working_directory //
  .workingDirectory //
  .tool_input.cwd //
  .tool_input.workdir //
  .toolInput.cwd //
  .toolInput.workdir //
  empty')
STATE_DIR="${HOME}/.claude/hooks/.state"
CALLGRAPH_FALLBACK_AFTER_FAILURES="${CLAUDE_CALLGRAPH_FALLBACK_AFTER_FAILURES:-2}"

if [ -z "$CMD" ]; then
  exit 0
fi

if ! [[ "$CALLGRAPH_FALLBACK_AFTER_FAILURES" =~ ^[0-9]+$ ]]; then
  CALLGRAPH_FALLBACK_AFTER_FAILURES=2
fi

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

allow_command_with_updated_input() {
  local reason="$1"
  local updated_cmd="$2"
  local mark_callgraph="${3:-0}"
  local original_input
  local updated_input

  original_input=$(echo "$INPUT" | jq -c '(.tool_input // .toolInput // {})')
  updated_input=$(echo "$original_input" | jq --arg cmd "$updated_cmd" '.command = $cmd')

  if [[ "$mark_callgraph" == "1" ]]; then
    mark_main_callgraph_usage
  fi

  reset_callgraph_failures
  jq -n \
    --arg reason "$reason" \
    --argjson updated "$updated_input" \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "allow",
        "permissionDecisionReason": $reason,
        "updatedInput": $updated
      }
    }'
  exit 0
}

deny_command() {
  jq -n \
    --arg reason "$1" \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
}

deny_with_callgraph_failure() {
  record_callgraph_failure
  deny_command "$1"
}

is_narrow_shell_fallback() {
  printf '%s' "$1" | grep -Eqi '^[[:space:]]*(rg|grep|find)\b' && \
    printf '%s' "$1" | grep -Eqi '(\|[[:space:]]*(head|tail)\b|--max-count\b|(^|[[:space:]])-m[[:space:]]+[0-9]+|sed[[:space:]]+-n)'
}

# Allow lightweight environment checks so agents can diagnose CallGraph availability.
if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*(which|command[[:space:]]+-v|type[[:space:]]+-P)[[:space:]]+callgraph\b'; then
  allow_command "Allowed: CallGraph availability check"
fi

if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*ls\b' && printf '%s' "$CMD" | grep -Eqi '(\.local/bin|\.dotnet/tools|/tools/?$|/tools/|callgraph)'; then
  allow_command "Allowed: CallGraph install/path inspection"
fi

# Allow explicit test-targeted shell exploration because test projects are excluded from CallGraph index scope.
if printf '%s' "$CMD" | grep -Eqi '\b(find|grep|rg|ls)\b' && \
   printf '%s' "$CMD" | grep -Eqi '((^|[/\\_.-])tests?([/\\_.-]|$)|\.tests?\.csproj\b|[._-]tests?\b|\b(xunit|nunit|mstest)\b)'; then
  allow_command "Allowed: test-targeted search is not rewritten because tests are excluded from CallGraph index scope"
fi

ORIGINAL_CMD="$CMD"

# Auto-correct common command issues before validation.
if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*callgraph[[:space:]]+analyze-callgraph\b'; then
  CMD=$(printf '%s' "$CMD" | perl -pe 's/^\s*callgraph\s+analyze-callgraph\b/callgraph analyze/i')
fi

if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*callgraph[[:space:]]+analyze\b'; then
  CMD=$(printf '%s' "$CMD" | perl -pe 's/--filePath\b/--filepath/ig')
fi

if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*callgraph[[:space:]]+get-method-source\b'; then
  if ! printf '%s' "$CMD" | grep -Eqi -- '--methodName([[:space:]]+|=)'; then
    CMD=$(printf '%s' "$CMD" | perl -pe 's/--method\b/--methodName/ig')
  fi
fi

# Guard against explosive internal callgraph traversals.
if printf '%s' "$CMD" | grep -Eqi '\bcallgraph\b' && printf '%s' "$CMD" | grep -Eqi '\banalyze\b'; then
  # analyze requires --filepath; provide corrective guidance early.
  if ! printf '%s' "$CMD" | grep -Eqi -- '--file(path|Path)([[:space:]]+|=)'; then
    deny_with_callgraph_failure "callgraph analyze requires --filepath <absolute-file.cs>. Example: callgraph analyze --filepath /abs/path/Foo.cs --method Bar --direction outbound --visibility external --depth 2 2>&1"
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
    deny_with_callgraph_failure "Blocked: callgraph analyze with --visibility internal supports max --depth 2. Use two-stage analysis: (1) map callers with inbound+external depth 2, (2) pick 1-3 candidates and run outbound+internal depth 2 per candidate."
  fi
fi

# Guard against large chained method-source extraction scripts.
if printf '%s' "$CMD" | grep -Eqi '\bcallgraph[[:space:]]+get-method-source\b'; then
  GET_METHOD_SOURCE_COUNT=$(printf '%s' "$CMD" | grep -Eo 'callgraph[[:space:]]+get-method-source' | wc -l | tr -d ' ')
  if [ "${GET_METHOD_SOURCE_COUNT:-0}" -gt 1 ] || printf '%s' "$CMD" | grep -Eq '&&|;'; then
    deny_with_callgraph_failure "Blocked: chained callgraph get-method-source commands are not allowed. Run one get-method-source call per command, then summarize; for multi-file inventory use callgraph list-methods --folderPath/--fileList first."
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
      allow_command_with_updated_input "CallGraph auto-rewrite: added --pattern for search-method" "$REWRITTEN_CMD" "1"
    fi
  fi
fi

# Allow callgraph commands (including output filtering like `callgraph ... | grep ...`).
if printf '%s' "$CMD" | grep -Eq '^[[:space:]]*callgraph\b'; then
  if [ "$CMD" != "$ORIGINAL_CMD" ]; then
    allow_command_with_updated_input "CallGraph auto-correction applied" "$CMD" "1"
  fi

  mark_main_callgraph_usage
  reset_callgraph_failures
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
  allow_command_with_updated_input "CallGraph auto-rewrite" "$REWRITTEN" "1"
fi

FAILURES=$(current_callgraph_failures)
if [ "$CALLGRAPH_FALLBACK_AFTER_FAILURES" -gt 0 ] && [ "$FAILURES" -ge "$CALLGRAPH_FALLBACK_AFTER_FAILURES" ] && is_narrow_shell_fallback "$CMD"; then
  allow_command "Allowed: narrow shell fallback after repeated CallGraph failures in this session"
fi

deny_with_callgraph_failure "C# code exploration should use CallGraph first. If this exact query cannot be rewritten, retry with search-file/list-methods/get-method-source. For explicit test-targeted queries, use a narrow shell fallback because test projects are excluded from the index."
