# /callgraph-analyze-callgraph

Run call graph analysis for a C# file (and optionally method) using CallGraph CLI.

## Command execution policy
- Run commands in foreground only and always append `2>&1`.
- Use daemon mode first for latency, then retry with `--no-daemon` only on timeout/error/inconsistent output.

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
- CallGraph index scope excludes test projects and the source files in those test projects.
- For explicit test-targeted discovery, use one narrow shell query instead of forcing `callgraph analyze`.
- If the target file is known, run `callgraph analyze` directly for that file.
- Do not run broader repo/project searches first unless the user explicitly asks for discovery.
- If user provides class + method but not file, resolve file first using `search-file --pattern "*<ClassName>.cs"`.
- Use `analyze` to find relationships and candidate hops, not to infer detailed filter/query behavior by itself.
- If the question is about behavior, data shaping, or query semantics, use `analyze` to narrow candidates, then inspect the downstream implementation with `/callgraph-get-method-source` or targeted reads until the real sink is found.

## Visibility (depth strategy)
Both modes traverse ALL edges including private/internal methods:
- `external`: Class-based depth. Same-class calls don't increment depth. Use for component-level analysis.
- `internal`: Method-based depth. Every call increments depth. Use for detailed tracing.
- Safety cap: when using `internal`, depth must be `<= 2`.
- If deeper tracing is needed, use two-stage analysis:
  1. map callers first with `--direction inbound --visibility external --depth 2`,
  2. pick 1-3 candidates and run `--direction outbound --visibility internal --depth 2` per candidate.

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

## Response format (line-based)
- Method rows:
  - `M\t<methodId>\t<filePath[:line]>\t<containingType>\t<methodName>`
- Call rows:
  - `C\t<callerMethodId>\t<calleeMethodId>\t<direction>`

Use these rows directly; do not assume JSON output for `analyze`.
