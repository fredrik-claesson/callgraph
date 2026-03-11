# /csharp-analyze-callgraph

Run call graph analysis for a C# file (and optionally method) using CallGraph CLI.

## Prereqs
- CallGraph CLI is available (`callgraph` binary or `dotnet run --project CallGraph.csproj --`)

## Inputs
Parse the user text after the command as:
- `file` (required): path to a `.cs` file (absolute preferred)
- `--method <MethodName>` (optional, case-sensitive)
- `--depth <n>` (optional, default 1)
- `--direction inbound|outbound|bi-directional` (optional, default bi-directional)
- `--visibility external|internal` (optional, default external)
- `--solutionPath` / `--solutionId` (optional)

## Scope rule
- If the target file is known, run `callgraph analyze` directly for that file.
- Do not run broader repo/project searches first unless the user explicitly asks for discovery.
- If user provides class + method but not file, resolve file first using `search-file --pattern "*<ClassName>.cs"`.

## Visibility (depth strategy)
Both modes traverse ALL edges including private/internal methods:
- `external`: Class-based depth. Same-class calls don't increment depth. Use for component-level analysis.
- `internal`: Method-based depth. Every call increments depth. Use for detailed tracing.

## Action
Run CLI:
`callgraph analyze --filepath <file> [--method <MethodName>] [--depth <n>] [--direction <value>] [--visibility <value>] [--solutionPath <path>] [--solutionId <id>]`

## Error handling
- **AmbiguousSolution**: Rerun with `--solutionPath` or `--solutionId`
- **TargetsNotFound**: Suggest providing `--method` or verify filepath
- **IndexNotReady**: Instruct user to run CLI with `--index`/`--reindex`
- If `--solutionPath`/`--solutionId` is omitted and exactly one indexed solution exists, the CLI auto-selects that solution.

## Output
Summarize node/edge counts and key inbound/outbound calls.

## Response format (raw JSON, reduced details)
- `methodCount`: number of methods returned
- `callCount`: number of call edges returned
- `methods`: array of:
  - `methodId` (short ID, e.g. `m1`, `m2`)
  - `methodName`
  - `containingType`
  - `filePath`
  - `startLine`
- `calls`: array of:
  - `callerMethodId`
  - `calleeMethodId`
  - `direction` (`inbound`/`outbound`)

Use `methods` + `calls` directly. Use this raw response directly.
