---
name: csharp-search-file
description: Fast indexed search for C# file paths via CallGraph CLI. Use when asked to find files by name or pattern.
---

# C# Search File (Indexed)

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
- `search-file` now returns streamlined JSON records directly.
