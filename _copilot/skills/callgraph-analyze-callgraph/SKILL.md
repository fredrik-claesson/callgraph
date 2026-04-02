---
name: callgraph-analyze-callgraph
description: Run CallGraph analysis for a C# file (optionally a method) via CLI and summarize inbound/outbound calls.
context: fork
agent: callgraph-haiku
---

# C# Analyze Call Graph

## Command execution policy
- Run commands in foreground only and always append `2>&1`.
- Use daemon mode first for latency, then retry with `--no-daemon` only on timeout/error/inconsistent output.

## Inputs
Parse the user request for:
- filepath (required)
- --method <name> (optional, case-sensitive)
- IMPORTANT: for `callgraph analyze`, the selector flag is `--method` (not `--methodName`, which is for `get-method-source`)
- --depth <n> (optional, default 1)
- --direction inbound|outbound|bi-directional (optional, default bi-directional)
- --visibility external|internal (optional, default external)
- --solutionPath / --solutionId (optional, for disambiguation)

## Scope rule
- CallGraph index scope excludes test projects and the source files in those test projects.
- For explicit test-targeted discovery, use one narrow shell query instead of forcing `callgraph analyze`.
- If the target file is known, run `callgraph analyze` directly on that file.
- Do not perform broader discovery/search first unless the user explicitly asks for it.
- If user provides class + method but no file path, resolve file first with `search-file --pattern "*<ClassName>.cs"`.
- When identifying candidate methods in a known file/folder, prefer scoped discovery flow: `list-methods` (scoped, live signatures) -> `search-method` (targeted index search) -> `get-method-source` (live body). Avoid bulk file reads until candidates are narrowed.
- Use `analyze` to find relationships and candidate hops, not to infer detailed filter/query behavior by itself.
- If the user is asking why data changes, which filter wins, or where a query is shaped, use `analyze` to narrow candidates, then inspect the downstream implementation with `get-method-source` or targeted reads until the real sink is found.

## Visibility (depth strategy)
- `external`: Class-based depth. Same-class calls don't increment depth. Use for component-level analysis.
- `internal`: Method-based depth. Every call increments depth. Use for detailed analysis.
- If a concrete method is known, prefer starting with `--visibility internal --depth 1` to keep output focused, then widen only if needed.
- Safety cap: when using `internal`, depth must be `<= 2`.
- If deeper tracing is needed, use two-stage analysis:
  1. map callers first with `--direction inbound --visibility external --depth 2`,
  2. pick 1-3 candidates and run `--direction outbound --visibility internal --depth 2` per candidate.

Both modes traverse ALL edges including private/internal methods.

## Action
Run CLI:
`callgraph analyze --filepath <file.cs> [--method <name>] [--depth <n>] [--direction <value>] [--visibility <value>] [--solutionPath <path>] [--solutionId <id>]`

## Error handling
- **AmbiguousSolution**: Rerun with solutionPath or solutionId
- **TargetsNotFound**: Suggest providing --method or verify filepath
- **IndexNotReady**: Instruct user to run CLI with `--index` or `--reindex`
- If solutionPath/solutionId is omitted and exactly one indexed solution exists, the CLI auto-selects that solution.

## Output
Summarize node/edge counts and key inbound/outbound calls.

## Response format (line-based)
- Method rows:
  - `M\t<methodId>\t<filePath[:line]>\t<containingType>\t<methodName>`
- Call rows:
  - `C\t<callerMethodId>\t<calleeMethodId>\t<direction>`

Use these rows directly; do not assume JSON output for `analyze`.
