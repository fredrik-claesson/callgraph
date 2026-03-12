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
- --depth <n> (optional, default 1)
- --direction inbound|outbound|bi-directional (optional, default bi-directional)
- --visibility external|internal (optional, default external)
- --solutionPath / --solutionId (optional, for disambiguation)

## Scope rule
- If the target file is known, run `callgraph analyze` directly on that file.
- Do not perform broader discovery/search first unless the user explicitly asks for it.
- If user provides class + method but no file path, resolve file first with `search-file --pattern "*<ClassName>.cs"`.

## Visibility (depth strategy)
- `external`: Class-based depth. Same-class calls don't increment depth. Use for component-level analysis.
- `internal`: Method-based depth. Every call increments depth. Use for detailed analysis.

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

## Response format (raw JSON, reduced details)
- `methodCount`: number of methods returned
- `callCount`: number of call edges returned
- `methods`: array of:
  - `methodId` (short ID, e.g. `m1`, `m2`)
  - `methodName`
  - `containingType`
  - `filePath`
  - `startLine`
- `calls`: array of:
  - `callerMethodId`
  - `calleeMethodId`
  - `direction` (`inbound`/`outbound`)

Use `methods` + `calls` directly. Use this raw response directly.
