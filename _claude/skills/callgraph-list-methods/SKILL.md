---
name: callgraph-list-methods
description: List indexed C# methods via CallGraph CLI with visibility filtering.
---

# C# List Methods (Indexed)

## Inputs
- --visibility (optional): `external` (default) or `internal`
- --solutionPath / --solutionId (optional): filter to specific solution

## Scope rule
- If the containing file is known, pass `--filePath <file.cs>` to keep results file-scoped.
- If only a folder is known, pass `--folderPath <folder>` before listing project-wide.

## Visibility
- `external` (default): public/protected/protected internal methods only.
- `internal`: all methods, including non-public.

## Action
Run CLI:
`callgraph list-methods [--visibility <external|internal>] [--solutionPath <path>] [--solutionId <id>] [--folderPath <folder>] [--filePath <file.cs>]`

## Output
- Show ranked list of methods (type + display + file + line + accessibility)
- If too many matches: suggest narrowing with solution/file scope

## Output format note
- `list-methods` now returns streamlined JSON records directly.
