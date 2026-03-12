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
- `depth` (optional, default 1)
- `direction`: inbound | outbound | bi-directional (default bi-directional)
- `visibility`: external | internal (default external)
- `solutionPath` / `solutionId`: Only when needed to disambiguate

## Scope rule
- If the target file is known, run `callgraph analyze` directly for that file.
- Do not run broader repo/project discovery first unless the user explicitly asks for it.
- If user provides class + method but not file, resolve file first via `search-file --pattern "*<ClassName>.cs"` before `analyze`.

## Visibility (depth strategy)
Both modes traverse ALL edges including private/internal methods:
- `external`: Class-based depth. Same-class calls don't increment depth. Use for component-level analysis.
- `internal`: Method-based depth. Every call increments depth. Use for detailed tracing.

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
