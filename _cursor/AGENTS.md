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
- For `callgraph analyze`, if `--visibility internal` is used, `--depth` must be `<= 2`.
- If deeper internals are needed, use two-stage analysis:
  1. map callers first with inbound + external depth 2,
  2. pick 1-3 candidates and run outbound + internal depth 2 per candidate.

## Workflow Scenarios
Select one scenario at the start and state it in one sentence.

`TopDownCallChain` (outbound-first): starting from a known entrypoint method, run
`callgraph-analyze-callgraph` outbound with `visibility=external` first, then
`visibility=internal` where needed; walk depth-by-depth until side effects/sinks,
collecting `method -> direct callees -> important awaits/state changes`.

- `UnknownEntrypoints`:
  - Find likely entrypoints quickly with `callgraph-search-file` + `callgraph-list-methods`.
  - Then switch to outbound call analysis (`visibility=external`, then `internal` for unclear/high-risk paths).
- `KnownEntrypoints`:
  - Use `TopDownCallChain` directly (outbound-first).
  - Confirm async/sync status along each chain before planning edits.
- `KnownComponentImpact`:
  - Start inbound (`external` then `internal`) to map callers and blast radius.
  - Run limited outbound from top-risk callers to confirm impact boundaries.
  - Produce caller-impact matrix: `caller | layer | change type | risk | confidence`.
- `LargeRefactorPlanning`:
  - Run `Map -> Deepen -> Synthesize`.
  - Map: scope inventory and chain overview.
  - Deepen: only hotspots/unknowns.
  - Synthesize: phased plan, risks, verification.

## Shared Workflow Rules
- Use elastic budgets: start with 10 discovery tool calls; expand by +8 only after a checkpoint.
- Required checkpoints:
  - `scope checkpoint`: `file | method(s) | why relevant | confidence`
  - `expansion checkpoint`: `unknowns | next tools | expected value`
- Prefer method-level discovery first (`callgraph-list-methods` / `callgraph-search-method` / `callgraph-analyze-callgraph` -> `callgraph-get-method-source`).
- Full-file `Read` is escalation only and requires an explicit reason.
- Keep full-file reads minimal (smallest relevant file, no broad sweeps).
- If two consecutive full-file reads yield no new findings, stop and checkpoint before further reads.
- Use Haiku subagents for bounded sidecar work (inventory/extraction), not final synthesis/tradeoff decisions.
- Keep parallel subagents small and independent; default max 2 unless justified.
