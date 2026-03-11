---
name: csharp-search-file
description: Fast indexed search for C# file paths via CallGraph CLI. Much faster than repo scanning.
metadata:
  short-description: "Indexed file search (CLI)"
---

## Why this skill
Queries a prebuilt analyzer index - much faster than scanning with `rg`/`find` on large solutions.

## Tool
Use CLI command: `callgraph search-file`
- MUST run in foreground (blocking).

## CLI Example
```bash
callgraph search-file --pattern "*Controller.cs" --solutionPath "C:\path\to\solution.sln"
```

## Parameters
- `pattern` (required): wildcards `*` and `?` (case-insensitive)
- `regex` (optional): use SQLite REGEXP instead of wildcards
- `solutionPath` (optional): filter to specific solution

## Scope rule
- If the exact file path is already known, skip `search-file` and use that path directly in downstream commands.
- If class name is known and file is unknown, prefer `search-file --pattern "*<ClassName>.cs"` before any semantic method search.
- If search is needed, start with the narrowest possible pattern.
- If no match, broaden pattern/scope incrementally in sequential retries.
- Do not run parallel/background search-file triangulation unless user explicitly asks.

## Output
- Show ranked list of matches (solutionPath + filePath)
- If 0 matches: suggest broader pattern or --regex
- If too many: suggest narrowing pattern or adding --solutionPath

## Output format note
- `search-file` now returns streamlined JSON records directly.

## Known issues
- In some Codex CLI sessions, CallGraph commands may still hang despite foreground execution.
- If that happens, rerun Codex with `--yolo`.
