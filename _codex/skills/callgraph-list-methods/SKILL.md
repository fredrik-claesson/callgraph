---
name: callgraph-list-methods
description: List indexed C# methods via CallGraph CLI with visibility filtering.
metadata:
  short-description: "Indexed method listing (CLI)"
---

## Why this skill
Returns a method inventory from the prebuilt CallGraph index without scanning source files. Useful when no name pattern is available.

## Tool
Use CLI command: `callgraph list-methods`
- Run in foreground and always append `2>&1`.
- Use daemon mode first, and retry with `--no-daemon` only on timeout/error/inconsistent output.

## CLI Example
```bash
callgraph list-methods --visibility external --solutionId "solution-id"
```

## Parameters
- `visibility` (optional): `external` (default) or `internal`
- `solutionPath` / `solutionId` (optional): filter to specific solution

## Scope rule
- If the containing file is known, pass `--filePath <file.cs>` to keep results file-scoped.
- If only a folder is known, pass `--folderPath <folder>` before listing project-wide.

## Visibility
- `external`: public/protected/protected internal methods only.
- `internal`: all methods, including non-public methods.

## Output
- Show ranked list of methods (type + display + file + line + accessibility)
- If too many: suggest narrowing scope with solution/folder/file options

## Output format note
- `list-methods` returns plain text, one match per line:
  `<filePath[:line]>\t<containingType>\t<methodName>\t<signature>`.

## Known issues
- In some Codex CLI sessions, CallGraph commands may still hang despite foreground execution.
- If that happens, rerun Codex with `--yolo`.
