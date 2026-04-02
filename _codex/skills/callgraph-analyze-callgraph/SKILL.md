---
name: callgraph-analyze-callgraph
description: Run CallGraph analysis for a C# file (optionally a method) via CLI and summarize inbound/outbound calls.
metadata:
  short-description: "CLI callgraph analyze summary"
---

## Why this skill
Use the CallGraph analyzer index instead of scanning source code. It is fast and accurate for large C# solutions.

## Tool
Use CLI command: `callgraph analyze`
- MUST run in foreground (blocking).
- Always append `2>&1` to command execution.
- Use daemon mode first, and retry with `--no-daemon` only on timeout/error/inconsistent output.

## CLI Example
```bash
callgraph analyze --filepath "C:\path\to\file.cs" --method "MethodName" --depth 1 --direction "bi-directional" --visibility "external" --solutionPath "C:\path\to\solution.sln"
```

## Parameters
- `filepath` (required): If relative, resolve against workspace
- `method` (optional, case-sensitive)
- IMPORTANT: for `callgraph analyze`, use `--method` (not `--methodName`, which is for `get-method-source`)
- `depth` (optional, default 1)
- `direction`: inbound | outbound | bi-directional (default bi-directional)
- `visibility`: external | internal (default external)
- `solutionPath` / `solutionId`: Only when needed to disambiguate

## Scope rule
- CallGraph index scope excludes test projects and the source files in those test projects.
- For explicit test-targeted discovery, use one narrow shell query instead of forcing `callgraph analyze`.
- If the target file is known, run `callgraph analyze` directly for that file.
- Do not run broader repo/project discovery first unless the user explicitly asks for it.
- If user provides class + method but not file, resolve file first via `search-file --pattern "*<ClassName>.cs"` before `analyze`.
- When identifying candidate methods in a known file/folder, prefer scoped discovery flow: `list-methods` (scoped, live signatures) -> `search-method` (targeted index search) -> `get-method-source` (live body). Avoid bulk file reads until candidates are narrowed.
- Use `analyze` to find relationships and candidate hops, not to infer detailed filter/query behavior by itself.
- If the question is about behavior, data shaping, or query semantics, use `analyze` to narrow candidates, then inspect the downstream implementation with `get-method-source` or targeted reads until the real sink is found.

## Visibility (depth strategy)
Both modes traverse ALL edges including private/internal methods:
- `external`: Class-based depth. Same-class calls don't increment depth. Use for component-level analysis.
- `internal`: Method-based depth. Every call increments depth. Use for detailed tracing.
- If a concrete method is known, prefer starting with `--visibility internal --depth 1` to keep output focused, then widen only if needed.
- Safety cap: when using `internal`, depth should be `<= 2`.

## Error handling
- **AmbiguousSolution**: Rerun with `solutionPath` or `solutionId`
- **TargetsNotFound**: Suggest providing `--method` or verify filepath
- **IndexNotReady**: Instruct user to run CLI with `--index`/`--reindex`
- If `solutionPath`/`solutionId` is omitted and exactly one indexed solution exists, the CLI auto-selects that solution.

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

## Known issues
- In some Codex CLI sessions, CallGraph commands may still hang despite foreground execution.
- If that happens, rerun Codex with `--yolo`.
