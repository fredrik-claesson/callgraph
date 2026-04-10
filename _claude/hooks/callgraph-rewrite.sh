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
CALLGRAPH_POLICY_MODE="${CLAUDE_CALLGRAPH_POLICY_MODE:-warn}"
CALLGRAPH_WARN_REDIRECT="${CLAUDE_CALLGRAPH_WARN_REDIRECT:-1}"

if [ -z "$CMD" ]; then
  exit 0
fi

if ! [[ "$CALLGRAPH_FALLBACK_AFTER_FAILURES" =~ ^[0-9]+$ ]]; then
  CALLGRAPH_FALLBACK_AFTER_FAILURES=2
fi

if [[ ! "$CALLGRAPH_POLICY_MODE" =~ ^(warn|deny)$ ]]; then
  CALLGRAPH_POLICY_MODE=warn
fi

if [[ ! "$CALLGRAPH_WARN_REDIRECT" =~ ^(0|1)$ ]]; then
  CALLGRAPH_WARN_REDIRECT=1
fi

is_git_commit_command() {
  printf '%s' "$1" | grep -Eqi '(^|[;&|()[:space:]])git[[:space:]]+commit([[:space:]]|$)'
}

extract_git_self_review_decision() {
  local command="$1"
  local value=""

  value=$(printf '%s' "$command" | sed -nE "s/.*\\$env:CALLGRAPH_GIT_SELF_REVIEW[[:space:]]*=[[:space:]]*['\"]?([A-Za-z_]+)['\"]?.*/\\1/ip" | head -n1)
  if [[ -z "$value" ]]; then
    value=$(printf '%s' "$command" | sed -nE "s/.*CALLGRAPH_GIT_SELF_REVIEW[[:space:]]*=[[:space:]]*['\"]?([A-Za-z_]+)['\"]?.*/\\1/p" | head -n1)
  fi

  printf '%s' "$value" | tr '[:upper:]' '[:lower:]'
}

git_commit_self_review_reason() {
  cat <<'EOF'
Before running git commit, ask the user if they want self-review.

If user declines: rerun commit with CALLGRAPH_GIT_SELF_REVIEW=skip.
If user accepts: describe intention/goal/purpose, run this PR2 deep-review workflow, and only then commit with CALLGRAPH_GIT_SELF_REVIEW=approved if review passes.

PR2 workflow required:
Phase 1 (context):
- If user provides PR number/URL: use gh pr view/gh pr diff/gh pr checks exactly.
- If no PR ref exists (pre-commit local mode): use git status --short, git diff --cached --name-only, git diff --name-only, git diff --cached, git diff; collect title/goal from the intention summary.
- Read every changed file fully.

Phase 2 (8 explicit independent passes):
1) Problem Resolution:
   - map each stated requirement to code changes
   - flag missing/partial implementation
   - flag non-goals changed unintentionally
2) Coding Conventions & Rules:
   - check naming, formatting/import ordering, file organization against surrounding code
   - check project patterns (error handling/logging/DI) and consistency with similar code
3) Obsolete / Deprecated Code:
   - flag deprecated APIs/features
   - flag migration-away patterns (TODO/FIXME/DEPRECATED hints)
   - flag outdated syntax
4) Test Coverage:
   - verify changed behavior is tested
   - identify missing branch/edge/error-path coverage
   - mark infeasible test cases explicitly as "infeasible, not a finding"
5) Race Conditions & Concurrency:
   - shared mutable state, TOCTOU, async interleaving, missing lock/channel/atomic safety
6) Performance:
   - N+1/query-in-loop, recomputation, large allocations/copies, missing caching, O(n^2)+ risks
7) Database Index Coverage:
   - index coverage for WHERE/ORDER BY/JOIN columns
   - full-scan risk, missing composite indexes, migration/index alignment
8) Future Flag Readiness & Dual-Mode Test Coverage:
   - assess whether a future feature flag is desirable
   - validate tests for both regression with flag OFF and new behavior with flag ON

For each pass, produce candidate findings with:
id, category, title, location, evidence, hypothesis.

Phase 3 (candidate list):
- Print:
  ## Candidate Findings (N total)
  [id] [category] [title]
        Location: [file:line]
        Evidence: [quote or reference]

Phase 4 (parallel validation):
- Launch one independent sub-agent per finding in parallel.
- Give each sub-agent full diff + finding details.
- Sub-agent returns ONLY:
  FINDING, VERDICT(valid|invalid|partial), IMPACT(critical|high|medium|low|informational), JUSTIFICATION(2-4 sentences), SUGGESTED FIX.

Phase 5 (final report):
- Produce:
  # PR Review Report
  Problem Resolution
  Validated Findings (Critical/High/Medium/Low-Informational)
  Discarded Findings
  Summary & Recommendation (APPROVE | REQUEST CHANGES | NEEDS DISCUSSION)

Commit is allowed only when review recommendation is APPROVE (no unresolved critical/high findings). Then run:
CALLGRAPH_GIT_SELF_REVIEW=approved git commit ...
EOF
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

if is_git_commit_command "$CMD"; then
  SELF_REVIEW_DECISION=$(extract_git_self_review_decision "$CMD")
  if [[ "$SELF_REVIEW_DECISION" == "approved" ]]; then
    allow_command "Allowed: git commit self-review approved marker detected"
  fi

  if [[ "$SELF_REVIEW_DECISION" == "skip" ]]; then
    allow_command "Allowed: git commit self-review skipped per explicit user decision"
  fi

  deny_command "$(git_commit_self_review_reason)"
fi

is_callgraph_first_policy_reason() {
  printf '%s' "$1" | grep -Fqi 'C# code exploration should use CallGraph first'
}

first_quoted_segment() {
  local text="$1"
  local quoted

  quoted=$(printf '%s' "$text" | sed -nE 's/.*"([^"]{2,})".*/\1/p' | head -n1)
  if [[ -n "$quoted" ]]; then
    printf '%s' "$quoted"
    return
  fi

  quoted=$(printf '%s' "$text" | sed -nE "s/.*'([^']{2,})'.*/\\1/p" | head -n1)
  printf '%s' "$quoted"
}

escape_for_single_quotes() {
  printf '%s' "$1" | sed "s/'/'\"'\"'/g"
}

extract_arg_value() {
  local command="$1"
  local flag="$2"
  local value=""

  value=$(printf '%s' "$command" | sed -nE "s/.*${flag}[[:space:]]+([^[:space:]]+).*/\\1/p" | head -n1)
  if [ -z "$value" ]; then
    value=$(printf '%s' "$command" | sed -nE "s/.*${flag}=([^[:space:]]+).*/\\1/p" | head -n1)
  fi

  value=$(printf '%s' "$value" | sed -E 's/^"//; s/"$//; s/^'\''//; s/'\''$//')
  printf '%s' "$value"
}

derive_warn_redirect_command() {
  local original="$1"
  local rewritten=""
  local canonical
  local no_daemon_suffix=""
  local file_pattern=""
  local query=""

  rewritten=$(callgraph rewrite --command "$original" 2>/dev/null) || rewritten=""
  if [[ -n "$rewritten" && "$rewritten" != "$original" ]]; then
    printf '%s' "$rewritten"
    return 0
  fi

  canonical=$(printf '%s' "$original" | sed -E 's/[[:space:]]+2>&1[[:space:]]*$//')
  if printf '%s' "$canonical" | grep -Eq -- '[[:space:]]+--no-daemon[[:space:]]*$'; then
    no_daemon_suffix=' --no-daemon'
    canonical=$(printf '%s' "$canonical" | sed -E 's/[[:space:]]+--no-daemon[[:space:]]*$//')
  fi

  file_pattern=$(printf '%s' "$canonical" | sed -nE "s/.*-name[[:space:]]+['\"]([^'\"]*\\.cs)['\"].*/\\1/p" | head -n1)
  if [[ -z "$file_pattern" ]]; then
    file_pattern=$(printf '%s' "$canonical" | sed -nE "s/.*--glob[[:space:]]+['\"]([^'\"]*\\.cs)['\"].*/\\1/p" | head -n1)
  fi
  if [[ -n "$file_pattern" ]]; then
    printf 'callgraph search-file --pattern "%s"%s 2>&1' "$file_pattern" "$no_daemon_suffix"
    return 0
  fi

  query=$(first_quoted_segment "$canonical")
  if [[ -z "$query" ]]; then
    return 1
  fi

  if printf '%s' "$query" | grep -Eqi '\.cs'; then
    local basename_query
    basename_query=$(basename "$query")
    printf 'callgraph search-file --pattern "*%s*"%s 2>&1' "$basename_query" "$no_daemon_suffix"
    return 0
  fi

  if printf '%s' "$query" | grep -Eq '^[A-Za-z_][A-Za-z0-9_]*$'; then
    printf 'callgraph search-method --pattern "*%s*"%s 2>&1' "$query" "$no_daemon_suffix"
    return 0
  fi

  printf "callgraph search-method --keywords '%s'%s 2>&1" "$(escape_for_single_quotes "$query")" "$no_daemon_suffix"
  return 0
}

deny_with_callgraph_failure() {
  local reason="$1"
  local redirected_cmd=""

  record_callgraph_failure
  if [[ "$CALLGRAPH_POLICY_MODE" == "warn" ]]; then
    if [[ "$CALLGRAPH_WARN_REDIRECT" == "1" ]] && is_callgraph_first_policy_reason "$reason" && command -v callgraph >/dev/null 2>&1; then
      redirected_cmd=$(derive_warn_redirect_command "$CMD") || redirected_cmd=""
      if [[ -n "$redirected_cmd" ]]; then
        allow_command_with_updated_input "High-priority CallGraph policy redirect: replaced shell exploration with CallGraph" "$redirected_cmd" "1"
      fi
    fi

    allow_command "High-priority CallGraph policy hint: $reason"
  fi

  deny_command "$reason"
}

is_narrow_shell_fallback() {
  printf '%s' "$1" | grep -Eqi '^[[:space:]]*(rg|grep|find)\b' && \
    printf '%s' "$1" | grep -Eqi '(\|[[:space:]]*(head|tail)\b|--max-count\b|(^|[[:space:]])-m[[:space:]]+[0-9]+|sed[[:space:]]+-n)'
}

canonicalize_callgraph_command() {
  printf '%s' "$1" | sed -E 's/[[:space:]]+2>&1[[:space:]]*$//' | tr -s '[:space:]' ' ' | sed -E 's/^[[:space:]]+|[[:space:]]+$//g'
}

callgraph_last_command_file() {
  local key
  key=$(session_key)
  printf '%s' "${STATE_DIR}/callgraph-last-command-${key}.txt"
}

callgraph_repeat_counter_file() {
  local key
  key=$(session_key)
  printf '%s' "${STATE_DIR}/callgraph-repeat-count-${key}.txt"
}

record_callgraph_command() {
  local command="$1"
  local canonical
  canonical=$(canonicalize_callgraph_command "$command")
  mkdir -p "$STATE_DIR"

  local last_file repeat_file previous repeat_count
  last_file=$(callgraph_last_command_file)
  repeat_file=$(callgraph_repeat_counter_file)
  previous=""
  if [[ -f "$last_file" ]]; then
    previous=$(cat "$last_file" 2>/dev/null || true)
  fi

  if [[ "$canonical" == "$previous" && -n "$canonical" ]]; then
    repeat_count=$(read_counter "$repeat_file")
    repeat_count=$((repeat_count + 1))
  else
    repeat_count=1
  fi

  printf '%s' "$canonical" > "$last_file"
  write_counter "$repeat_file" "$repeat_count"
  printf '%s' "$repeat_count"
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
  CMD=$(printf '%s' "$CMD" | perl -pe 's/--methodName\b/--method/ig')
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
CALLGRAPH_CMD_CANONICAL=$(printf '%s' "$CMD" | sed -E 's/[[:space:]]+2>&1[[:space:]]*$//')
CALLGRAPH_NO_DAEMON_SUFFIX=""
if printf '%s' "$CALLGRAPH_CMD_CANONICAL" | grep -Eq -- '[[:space:]]+--no-daemon[[:space:]]*$'; then
  CALLGRAPH_NO_DAEMON_SUFFIX=' --no-daemon'
  CALLGRAPH_CMD_CANONICAL=$(printf '%s' "$CALLGRAPH_CMD_CANONICAL" | sed -E 's/[[:space:]]+--no-daemon[[:space:]]*$//')
fi

if printf '%s' "$CALLGRAPH_CMD_CANONICAL" | grep -Eq '^[[:space:]]*callgraph[[:space:]]+search-method\b' && \
   ! printf '%s' "$CALLGRAPH_CMD_CANONICAL" | grep -Eq -- '--(pattern|keywords|regex)\b'; then
  BARE_QUERY=$(printf '%s' "$CALLGRAPH_CMD_CANONICAL" | sed -nE 's/^[[:space:]]*callgraph[[:space:]]+search-method[[:space:]]+("?[^-][^"]*"?)[[:space:]]*$/\1/p' | head -n1)
  if [ -n "$BARE_QUERY" ]; then
    BARE_QUERY=$(printf '%s' "$BARE_QUERY" | sed -E 's/^"//; s/"$//; s/^'\''//; s/'\''$//')
    if [ -n "$BARE_QUERY" ]; then
      REWRITTEN_CMD=$(printf 'callgraph search-method --pattern "*%s*"%s 2>&1' "$BARE_QUERY" "$CALLGRAPH_NO_DAEMON_SUFFIX")
      allow_command_with_updated_input "CallGraph auto-rewrite: added --pattern for search-method" "$REWRITTEN_CMD" "1"
    fi
  fi
fi

# Rewrite a common malformed search-file invocation:
#   callgraph search-file FooBar  -> callgraph search-file --pattern "*FooBar*"
if printf '%s' "$CALLGRAPH_CMD_CANONICAL" | grep -Eq '^[[:space:]]*callgraph[[:space:]]+search-file\b' && \
   ! printf '%s' "$CALLGRAPH_CMD_CANONICAL" | grep -Eq -- '--(pattern|regex)\b'; then
  BARE_QUERY=$(printf '%s' "$CALLGRAPH_CMD_CANONICAL" | sed -nE 's/^[[:space:]]*callgraph[[:space:]]+search-file[[:space:]]+("?[^-][^"]*"?)[[:space:]]*$/\1/p' | head -n1)
  if [ -n "$BARE_QUERY" ]; then
    BARE_QUERY=$(printf '%s' "$BARE_QUERY" | sed -E 's/^"//; s/"$//; s/^'\''//; s/'\''$//')
    if [ -n "$BARE_QUERY" ]; then
      REWRITTEN_CMD=$(printf 'callgraph search-file --pattern "*%s*"%s 2>&1' "$BARE_QUERY" "$CALLGRAPH_NO_DAEMON_SUFFIX")
      allow_command_with_updated_input "CallGraph auto-rewrite: added --pattern for search-file" "$REWRITTEN_CMD" "1"
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
  REPEAT_COUNT=$(record_callgraph_command "$CMD")
  if [ "${REPEAT_COUNT:-1}" -ge 2 ]; then
    allow_command "Hint: identical CallGraph command repeated in this session (${REPEAT_COUNT}x). Reuse previous evidence unless scope changed or prior output was inconclusive"
  fi

  if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*callgraph[[:space:]]+get-method-source\b' && \
     ! printf '%s' "$CMD" | grep -Eqi -- '--mode([[:space:]]+|=)'; then
    allow_command "Hint: prefer callgraph get-method-source --mode body_only for token-efficient method reads (use signature_plus_body only when signatures are explicitly needed)"
  fi

  if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*callgraph[[:space:]]+analyze\b'; then
    HAS_METHOD_FLAG=0
    if printf '%s' "$CMD" | grep -Eqi -- '--method([[:space:]]+|=)'; then
      HAS_METHOD_FLAG=1
    fi

    DEPTH_HINT=$(extract_arg_value "$CMD" '--depth')
    if [ -z "$DEPTH_HINT" ]; then
      DEPTH_HINT=1
    fi

    if [ "$HAS_METHOD_FLAG" -eq 0 ] && [ "$DEPTH_HINT" -gt 1 ] 2>/dev/null; then
      allow_command "Hint: analyze without --method can explode output. If you have a concrete symbol, use --method <Name> and start with --visibility internal --depth 1, then widen only if needed"
    fi
  fi

  if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*callgraph[[:space:]]+search-method\b' && \
     printf '%s' "$CMD" | grep -Eqi -- '--keywords([[:space:]]+|=)' && \
     ! printf '%s' "$CMD" | grep -Eqi -- '--pattern([[:space:]]+|=)'; then
    KEYWORDS_HINT=$(extract_arg_value "$CMD" '--keywords')
    if printf '%s' "$KEYWORDS_HINT" | grep -Eq '^[A-Za-z_][A-Za-z0-9_]*$'; then
      allow_command "Hint: for identifier-known lookup, prefer search-method --pattern \"*${KEYWORDS_HINT}*\" (plus --filePath/--solutionPath scope) over --keywords"
    fi
  fi

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

deny_with_callgraph_failure "CallGraph-first policy: do not use rg/find/grep for C# discovery before trying CallGraph. Run callgraph search-file/search-method/list-methods/get-method-source first (daemon, then --no-daemon on failure). Shell fallback is allowed only for explicit test-targeted queries or after repeated CallGraph failures."
