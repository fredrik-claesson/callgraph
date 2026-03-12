---
name: callgraph-list-warnings
description: List warning diagnostics for a C# project via CallGraph CLI. Faster than manual scanning.
metadata:
  short-description: "Warning diagnostics (CLI)"
---

## Why this skill
Queries a prebuilt analyzer index to surface compiler warnings, code quality issues, and analyzer warnings. Much faster than scanning with `rg`/`grep` on large solutions. Returns structured diagnostic metadata.

## Tool
Use CLI command: `callgraph list-warnings`

## CLI Example
```bash
callgraph list-warnings --projectPath "MyProject.csproj" --filePath "/abs/path/to/File.cs"
```

## Parameters
- `projectPath` (required): absolute or relative .csproj path (relative resolved by suffix matching)
- `filePath` (required): absolute path to a specific `.cs` file

## Scope rule
- Hard requirement: always provide both `projectPath` and `filePath`.
- `folderPath` is not supported for this command.

## Command construction rules
- Never emit `--filePath` without a non-empty value.
- If multiple files are in scope (for example branch changes), run one command per file.
- Do not run `.NET` compile/test commands (`dotnet build`, `dotnet test`, `dotnet restore`) to gather warnings.
- Use only CallGraph output for warning results.
- Use canonical CLI only: `callgraph list-warnings` (never `callgraph warnings`).

## Branch-change workflow
- When asked about "this branch", collect changed `.cs` files first.
- For each file, resolve its owning `.csproj`, then run:
  `callgraph list-warnings --projectPath <project.csproj> --filePath <changed-file.cs>`
- Aggregate results by file. Do not broaden to project-level scans unless explicitly requested.
- Do not claim warnings are "newly introduced" unless compared against a base revision.

## Batch cache strategy (2 minutes)
- For batch requests with multiple files in the same project, maintain an in-memory cache keyed by absolute `projectPath` with TTL 120 seconds.
- Cache value: full output for the project, reused internally by the CLI cache.
- For subsequent files in the same project within TTL, reuse cached output and filter by target `filePath` instead of re-running CLI.
- If cache is expired or missing, refresh once per project, then reuse for remaining files in that project.
- In single-file requests, call with both `--projectPath` and `--filePath`.

## Output
- Show ranked list of warnings (severity + message + file + line)
- If 0 results: report no warnings detected in scope

## Response format (raw JSON)
- `totalCount`
- `returnedCount`
- `truncated`
- `diagnostics`: array of:
  - `id`
  - `severity`
  - `message`
  - `filePath`
  - `startLine`
  - `startColumn`
  - `endLine`
  - `endColumn`

## Known issues
- In some Codex CLI sessions, CallGraph commands may still hang despite foreground execution.
- If that happens, rerun Codex with `--yolo`.
