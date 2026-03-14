---
name: callgraph-list-methods
description: List live C# methods via CallGraph CLI with visibility filtering.
---

# C# List Methods (Live)

## Command execution policy
- Run commands in foreground only and always append `2>&1`.
- Use daemon mode first for latency, then retry with `--no-daemon` only on timeout/error/inconsistent output.

## Inputs
- --visibility (optional): `external` (default) or `internal`
- --solutionPath / --solutionId (optional): filter to specific solution

## Scope rule
- If the containing file is known, pass `--filePath <file.cs>` to keep results file-scoped.
- If you need multiple specific files, pass `--fileList <path>` (newline-delimited absolute `.cs` paths) instead of shell `for` loops.
- If only a folder is known, pass `--folderPath <folder>` before listing project-wide.
- When identifying candidate methods in a known file/folder, prefer scoped discovery flow: `list-methods` (scoped, live signatures) -> `search-method` (targeted index search) -> `get-method-source` (live body). Avoid bulk file reads until candidates are narrowed.

## Live source follow-up
- Use `callgraph get-method-source` for exact implementation text after selecting a row:
  `callgraph get-method-source --filePath <file.cs> --methodName <name> --containingType <type> --startLine <line> --mode body_only`

## Visibility
- `external` (default): public/protected/protected internal methods only.
- `internal`: all methods, including non-public.

## Action
Run CLI:
`callgraph list-methods [--visibility <external|internal>] [--solutionPath <path>] [--solutionId <id>] [--folderPath <folder>] [--filePath <file.cs>] [--fileList <path>]`

## Output
- Show ranked list of methods (type + display + file + line + accessibility)
- If too many matches: suggest narrowing with solution/file scope

## Output format note
- `list-methods` returns plain text, one match per line:
  `<filePath[:line]>\t<containingType>\t<methodName>\t<signature>`.
