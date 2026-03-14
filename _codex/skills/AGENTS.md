# C# Code Intelligence
NEVER use `Grep`, `Glob`, or shell commands (`rg`, `find`, `grep`) for C# code searches.
ALWAYS use CallGraph skills instead:
- Find methods by name/pattern → `callgraph-search-method`
- Find files by name → `callgraph-search-file`
- List methods in a class/file → `callgraph-list-methods`
- Read exact live method content from file → `callgraph-get-method-source`
- Trace call dependencies → `callgraph-analyze-callgraph`
- Semantic/exploratory searches → `callgraph-search-method`
- Planning and gathering context → `callgraph-analyze-callgraph`

When spawning sub-agents for C# exploration, always include this instruction
explicitly in the prompt: "Use CallGraph skills (callgraph-search-method,
callgraph-list-methods, callgraph-analyze-callgraph, and callgraph-get-method-source instead of grep/rg/find."

Command execution policy for CallGraph:
- Always run foreground/blocking commands and always append `2>&1`.
- Use daemon mode first for latency: `callgraph <command> ... 2>&1`.
- Retry with `--no-daemon` only on timeout/error/inconsistent output:
  `callgraph <command> ... --no-daemon 2>&1`.
- Use shell `rg`/`find` only as a last-resort fallback after CallGraph retry still fails to locate targets, and keep fallback to one narrow query.

# Discovery Playbooks
Before discovery tool usage, select exactly one playbook and state it in one sentence.

## Playbooks
- `TopDownCallChain`: Start from known entrypoints, run `callgraph-analyze-callgraph` outbound depth 2, then inspect only matched methods.
- `PatternSweep`: Run broad `callgraph-search-method`/`callgraph-search-file` to find candidates, then narrow with `callgraph-list-methods` and method-source reads.
- `Hybrid` (default): Quick `PatternSweep` shortlist, then `TopDownCallChain` verification on shortlist.

## Selection Rules
- Known stable entrypoints: `TopDownCallChain`.
- Unclear entrypoints or inconsistent naming: `PatternSweep`.
- Broad or uncertain scope: `Hybrid`.

## Guardrails
- Discovery budget: max 10 tool calls before first shortlist.
- No full-file reads before shortlist; prefer `callgraph-get-method-source`.
- After shortlist, provide checkpoint table: `file`, `method`, `reason`, `confidence`.
- If confidence is below `0.8` after budget, switch playbook once; then ask one focused question.
- Avoid duplicate reads unless previous attempt failed.
