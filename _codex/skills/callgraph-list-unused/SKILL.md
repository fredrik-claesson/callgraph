---
name: callgraph-list-unused
description: List unused code diagnostics for a C# project via CallGraph CLI. Faster than manual scanning.
metadata:
  short-description: "Unused code diagnostics (CLI)"
---

## Why this skill
Queries a prebuilt analyzer index to surface unused members, dead code, and unreferenced symbols. Much faster than scanning with `rg`/`grep` on large solutions. Returns structured diagnostic metadata.

## Tool
Use CLI command: `callgraph list-unused`
- Run in foreground and always append `2>&1`.
- Use daemon mode first, and retry with `--no-daemon` only on timeout/error/inconsistent output.

## CLI Example
```bash
callgraph list-unused --projectPath "MyProject.csproj" --filePath "/abs/path/to/File.cs"
```

## Parameters
- `projectPath` (required): absolute or relative .csproj path (relative resolved by suffix matching)
- `filePath` (required): absolute path to a specific `.cs` file

## Scope rule
- Hard requirement: always provide both `projectPath` and `filePath`.
- `folderPath` is not supported for this command.
- When identifying candidate methods in a known file/folder, prefer scoped discovery flow: `list-methods` (scoped, live signatures) -> `search-method` (targeted index search) -> `get-method-source` (live body). Avoid bulk file reads until candidates are narrowed.

## Command construction rules
- Never emit `--filePath` without a non-empty value.
- If multiple files are in scope (for example branch changes), run one command per file.
- Do not run `.NET` compile/test commands (`dotnet build`, `dotnet test`, `dotnet restore`) to gather diagnostics.
- Use only CallGraph output for unused-code results.

## Branch-change workflow
- When asked about "this branch", collect changed `.cs` files first.
- For each file, resolve its owning `.csproj`, then run:
  `callgraph list-unused --projectPath <project.csproj> --filePath <changed-file.cs>`
- Aggregate results by file. Do not broaden to project-level scans unless explicitly requested.

## Output
- Show ranked list of unused diagnostics (severity + message + file + line)
- If 0 results: report no unused code detected in scope

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
