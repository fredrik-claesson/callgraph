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
CALLGRAPH_POLICY_MODE="${COPILOT_CALLGRAPH_POLICY_MODE:-warn}"

if ! [[ "$CALLGRAPH_FALLBACK_AFTER_FAILURES" =~ ^[0-9]+$ ]]; then
  CALLGRAPH_FALLBACK_AFTER_FAILURES=2
fi

if [[ ! "$CALLGRAPH_POLICY_MODE" =~ ^(warn|deny)$ ]]; then
  CALLGRAPH_POLICY_MODE=warn
fi

deny() {
  jq -nc --arg reason "$1" '{"permissionDecision":"deny","permissionDecisionReason":$reason}'
  exit 0
}

allow() {
  jq -nc --arg reason "$1" '{"permissionDecision":"allow","permissionDecisionReason":$reason}'
  exit 0
}

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

deny_with_callgraph_failure() {
  local reason="$1"
  record_callgraph_failure
  if [[ "$CALLGRAPH_POLICY_MODE" == "warn" ]]; then
    allow "Hint: $reason"
  fi

  deny "$reason"
}

is_narrow_shell_fallback() {
  printf '%s' "$1" | grep -Eqi '^[[:space:]]*(rg|grep|find)\b' && \
    printf '%s' "$1" | grep -Eqi '(\|[[:space:]]*(head|tail)\b|--max-count\b|(^|[[:space:]])-m[[:space:]]+[0-9]+|sed[[:space:]]+-n)'
}

extract_arg() {
  local command="$1"
  local flag="$2"
  local value
  value=$(printf '%s' "$command" | sed -nE "s/.*${flag}[[:space:]]+([^[:space:]]+).*/\\1/p" | head -n1)
  if [[ -z "$value" ]]; then
    value=$(printf '%s' "$command" | sed -nE "s/.*${flag}=([^[:space:]]+).*/\\1/p" | head -n1)
  fi

  printf '%s' "$value" | sed -E 's/^"//; s/"$//; s/^'\''//; s/'\''$//'
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

# Non-shell tools are out of scope for this policy.
if [[ "$TOOL_NAME" != "bash" && "$TOOL_NAME" != "powershell" ]]; then
  exit 0
fi

if [[ -z "$CMD" ]]; then
  exit 0
fi

if is_git_commit_command "$CMD"; then
  SELF_REVIEW_DECISION=$(extract_git_self_review_decision "$CMD")
  if [[ "$SELF_REVIEW_DECISION" == "approved" ]]; then
    allow "Allowed: git commit self-review approved marker detected"
  fi

  if [[ "$SELF_REVIEW_DECISION" == "skip" ]]; then
    allow "Allowed: git commit self-review skipped per explicit user decision"
  fi

  deny "$(git_commit_self_review_reason)"
fi

# Allow test-targeted shell exploration because tests are excluded from CallGraph index scope.
if printf '%s' "$CMD" | grep -Eqi '\b(find|grep|rg|ls)\b' && \
   printf '%s' "$CMD" | grep -Eqi '((^|[/\\_.-])tests?([/\\_.-]|$)|\.tests?\.csproj\b|[._-]tests?\b|\b(xunit|nunit|mstest)\b)'; then
  exit 0
fi

# Guard against common callgraph usage errors.
if printf '%s' "$CMD" | grep -Eqi '\bcallgraph\b' && printf '%s' "$CMD" | grep -Eqi '\banalyze\b'; then
  if printf '%s' "$CMD" | grep -Eqi -- '--methodName([[:space:]]+|=)'; then
    deny_with_callgraph_failure 'callgraph analyze uses --method (not --methodName). Example: callgraph analyze --filepath /abs/path/Foo.cs --method Bar --direction inbound --visibility internal --depth 1 2>&1'
  fi

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
  REPEAT_COUNT=$(record_callgraph_command "$CMD")

  if [[ "${REPEAT_COUNT:-1}" -ge 2 ]]; then
    allow "Hint: identical CallGraph command repeated in this session (${REPEAT_COUNT}x). Reuse previous evidence unless scope changed or prior output was inconclusive."
  fi

  if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*callgraph[[:space:]]+get-method-source\b' && \
     ! printf '%s' "$CMD" | grep -Eqi -- '--mode([[:space:]]+|=)'; then
    allow 'Hint: prefer callgraph get-method-source --mode body_only for token-efficient method reads.'
  fi

  if printf '%s' "$CMD" | grep -Eqi '^[[:space:]]*callgraph[[:space:]]+search-method\b' && \
     printf '%s' "$CMD" | grep -Eqi -- '--keywords([[:space:]]+|=)' && \
     ! printf '%s' "$CMD" | grep -Eqi -- '--pattern([[:space:]]+|=)'; then
    KEYWORDS=$(extract_arg "$CMD" "--keywords")
    if printf '%s' "$KEYWORDS" | grep -Eq '^[A-Za-z_][A-Za-z0-9_]*$'; then
      allow "Hint: for identifier-known lookup, prefer callgraph search-method --pattern \"*${KEYWORDS}*\" with scope (--filePath/--solutionPath)."
    fi
  fi

  exit 0
fi

# Enforce CallGraph-first for C# shell exploration patterns.
if printf '%s' "$CMD" | grep -Eqi '\b(find|grep|rg|ls)\b' && \
   printf '%s' "$CMD" | grep -Eqi '(\.cs([^[:alnum:]_]|$)|-name[[:space:]]+"?\*?\.cs|/src|xargs[[:space:]]+grep)'; then
  FAILURES=$(current_callgraph_failures)
  if [[ "$CALLGRAPH_FALLBACK_AFTER_FAILURES" -gt 0 ]] && [[ "$FAILURES" -ge "$CALLGRAPH_FALLBACK_AFTER_FAILURES" ]] && is_narrow_shell_fallback "$CMD"; then
    exit 0
  fi

  deny_with_callgraph_failure 'CallGraph-first policy: do not use rg/find/grep for C# discovery before trying CallGraph. Run callgraph search-file/search-method/list-methods/get-method-source first (daemon, then --no-daemon on failure). Shell fallback is allowed only for explicit test-targeted queries or after repeated CallGraph failures.'
fi

# Allow all other commands.
exit 0
