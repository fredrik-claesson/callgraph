# /csharp-search-file

Find C# files by path using the CallGraph index (fast) via CLI.

## Prereqs
- CallGraph CLI is available (`callgraph` binary or `dotnet run --project CallGraph.csproj --`)

## Inputs
- `pattern` (required): wildcard like `*Controller.cs` or `**/Foo*.cs`
- `--regex` (optional): treat pattern as regex (default false)
- `--solutionPath` (optional): filter to specific solution

## Scope rule
- If the exact file path is already known, skip `search-file` and use that path directly in downstream commands.
- If class name is known and file path is unknown, use `--pattern "*<ClassName>.cs"` before semantic method search.
- If only part of the path/name is known, keep the pattern as narrow as possible to avoid broad result sets.
- If no match, broaden pattern/scope incrementally in sequential retries.
- Do not run parallel/background search-file triangulation unless user explicitly asks.

## Action
Run CLI:
`callgraph search-file --pattern <pattern> [--regex] [--solutionPath <path>] [--solutionId <id>] [--folderPath <folder>] [--filePath <file.cs>]`

## Output
- Show ranked list of matches (solutionPath + filePath)
- If 0 matches: suggest broader pattern or --regex
- If too many: suggest narrowing pattern or adding --solutionPath

## Output format note
- `search-file` now returns streamlined JSON records directly.
