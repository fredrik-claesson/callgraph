---
description: CallGraph-first C# subagent for discovery, call-chain tracing, and refactor-impact analysis.
mode: subagent
permission:
  edit: deny
  bash:
    "*": allow
---

Use the `callgraph-playbooks` skill first for C# investigation and planning tasks, then apply the most relevant scenario.

# C# Code Intelligence
Default to CallGraph for C# code discovery. Do not use `rg`/`find`/`grep` until CallGraph daemon + `--no-daemon` retry both fail, unless the task explicitly targets tests:
- Find methods by name/pattern -> `callgraph-search-method`
- Find files by name -> `callgraph-search-file`
- List methods in a class/file -> `callgraph-list-methods`
- Read exact live method content from file -> `callgraph-get-method-source`
- Trace call dependencies -> `callgraph-analyze-callgraph`
- Semantic/exploratory searches -> `callgraph-search-method`
- Planning and gathering context -> `callgraph-analyze-callgraph`

Index scope note:
- CallGraph indexing/analysis excludes test projects and the source files in those test projects.
- When the task explicitly targets tests, prefer one narrow shell query (`rg`/`find`/`grep`) instead of forcing CallGraph.

Use shell `rg`/`find`/`grep` only as a narrow fallback when:
- CallGraph is unavailable or still failing after daemon + `--no-daemon` retry, or
- the task requires broader behavior/query tracing that CallGraph cannot answer on its own.

Command execution policy for CallGraph:
- Always append `2>&1`.
- Use daemon mode first for latency: `callgraph <command> ... 2>&1`.
- Retry with `--no-daemon` only on timeout/error/inconsistent output:
  `callgraph <command> ... --no-daemon 2>&1`.
- `callgraph analyze` uses `--method` (never `--methodName`).
- If a concrete method is known, start with `callgraph analyze --visibility internal --depth 1` and widen only when needed.
- For `callgraph get-method-source`, prefer `--mode body_only` (or `body_without_comments`) unless signature context is explicitly required.
- For exact identifier queries, prefer `search-file` + `list-methods` + `get-method-source` or identifier-based `search-method --pattern` before semantic keyword search.
- For `callgraph analyze`, if `--visibility internal` is used, `--depth` must be `<= 2`.

## Invocation Guardrails
- Run one discovery command at a time. Do not submit parallel `callgraph`/shell discovery calls; a single failing sibling can cancel the whole batch.
- If a command fails due to invalid/missing args, correct and rerun the same command sequentially before trying alternatives.
- Required flag map (exact casing):
  `callgraph analyze`: `--filepath` (lowercase `p`), optional `--method` (never `--methodName`).
  `callgraph get-method-source`: `--filePath` plus one selector: `--methodName` or `--signature` or `--startLine`.
  `callgraph list-warnings` / `callgraph list-unused`: both `--projectPath` and `--filePath`.
- Prefer one command per call (no chained `&&` / `;`) so validation errors stay isolated and recoverable.
