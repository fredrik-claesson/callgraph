# /callgraph-list-methods

List indexed C# methods using CallGraph CLI with visibility filtering.

## Command execution policy
- Run commands in foreground only and always append `2>&1`.
- Use daemon mode first for latency, then retry with `--no-daemon` only on timeout/error/inconsistent output.

## Prereqs
- CallGraph CLI is available (`callgraph` binary or `dotnet run --project CallGraph.csproj --`)

## Inputs
- `--visibility` (optional): `external` (default) or `internal`
- `--solutionPath` / `--solutionId` (optional): filter to specific solution

## Scope rule
- If the containing file is known, include `--filePath <file.cs>` to keep results file-scoped.
- If only a folder is known, include `--folderPath <folder>` before listing project-wide.
- Use project-wide method listing only when explicitly requested.

## Visibility
- `external` (default): public/protected/protected internal methods only
- `internal`: includes all methods (including non-public)

## Action
Run CLI:
`callgraph list-methods [--visibility <external|internal>] [--solutionPath <path>] [--solutionId <id>] [--folderPath <folder>] [--filePath <file.cs>]`

## Output
- Show ranked list of methods (type + display + file + line + accessibility)
- If too many: suggest narrowing with solution/folder/file filters

## Output format note
- `list-methods` returns plain text, one match per line:
  `<filePath[:line]>\t<containingType>\t<methodName>\t<signature>`.
