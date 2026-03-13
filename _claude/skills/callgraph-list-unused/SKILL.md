---
name: callgraph-list-unused
description: List unused code diagnostics for a C# project via CallGraph CLI. Use when asked to find dead code, unused members, or unreferenced symbols.
---

# C# List Unused (Diagnostics)

## Command execution policy
- Run commands in foreground only and always append `2>&1`.
- Use daemon mode first for latency, then retry with `--no-daemon` only on timeout/error/inconsistent output.

## Inputs
- --projectPath (required): absolute or relative .csproj path
- --filePath (required): absolute path to a specific `.cs` file

## Scope rule
- Hard requirement: always provide both `--projectPath` and `--filePath`.
- `--folderPath` is not supported for this command.
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

## Action
Run CLI:
`callgraph list-unused --projectPath <project.csproj> --filePath <file.cs>`

## Error handling
- **ValidationFailed**: `--filePath` is required and `--folderPath` is not supported
- **AmbiguousSolution**: Rerun with full absolute projectPath
- **IndexNotReady**: Instruct user to run CLI with `--index` or `--reindex`

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
