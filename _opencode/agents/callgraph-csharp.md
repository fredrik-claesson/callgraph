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
Use CallGraph skills first for C# code discovery whenever they can answer the question precisely:
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
- Always run foreground/blocking commands and always append `2>&1`.
- Use daemon mode first for latency: `callgraph <command> ... 2>&1`.
- Retry with `--no-daemon` only on timeout/error/inconsistent output:
  `callgraph <command> ... --no-daemon 2>&1`.
- For exact identifier queries, prefer `search-file` + `list-methods` + `get-method-source` or identifier-based `search-method --pattern` before semantic keyword search.
- For `callgraph analyze`, if `--visibility internal` is used, `--depth` must be `<= 2`.
