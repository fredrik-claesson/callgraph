---
name: callgraph-search-file
description: Fast indexed search for C# file paths via CallGraph CLI. Use when asked to find files by name or pattern.
---

# C# Search File (Indexed)

## Command execution policy
- Run commands in foreground only and always append `2>&1`.
- Use daemon mode first for latency, then retry with `--no-daemon` only on timeout/error/inconsistent output.

## Inputs
- pattern (required): wildcard like `*Controller.cs` or `**/Foo*.cs`
- --regex (optional): treat pattern as regex (default false)
- --solutionPath (optional): filter to specific solution

## Scope rule
- If the exact file path is already known, skip this skill and use the file path directly in downstream commands.
- If class name is known and file path is unknown, prefer `--pattern "*<ClassName>.cs"` before semantic method lookup.
- If search is needed, use the narrowest possible pattern first.
- If no match, broaden pattern/scope incrementally in sequential retries.
- Do not run parallel/background search-file triangulation unless user explicitly asks.

## Action
Run CLI:
`callgraph search-file --pattern <pattern> [--regex] [--solutionPath <path>] [--solutionId <id>] [--folderPath <folder>] [--filePath <file.cs>]`

## Output
- Show ranked list of matches (solutionPath + filePath)
- If 0 matches: suggest broader pattern or --regex
- If too many matches: suggest narrowing pattern or adding --solutionPath

## Output format note
- `search-file` returns plain text with one file path per line.
