# CallGraph CLI

CallGraph is a .NET CLI tool that indexes C# solutions with Roslyn and stores a local SQLite index for fast call graph analysis, search, and diagnostics.

## Purpose

CallGraph exists to improve coding-agent and developer workflows for C# codebases by:

- providing precise, local code intelligence (search, call graph analysis, diagnostics, and live method source extraction)
- reducing token usage and response latency by replacing broad prompt-based code scanning with targeted local CLI queries
- shipping agent skill templates in `_claude`, `_codex`, and `_cursor` so agents can use consistent, high-precision CallGraph workflows

## Key Features

- Direct and interface-based dependency resolution using Roslyn Analyzer
- Inbound/outbound/bi-directional call graph traversal
- Indexed file and method search
- Hybrid method search: soft OR lexical retrieval + soft AND lexical ranking, then top-N semantic rerank (local bge-small-en-v1.5)
- Method listing with live signature refresh and external/internal visibility filter
- Warning and unused diagnostics for projects
- Incremental reindexing with file watching

## Build & Install

### 1. Publish

```bash
dotnet publish ./CallGraph.csproj -c Release -r osx-arm64 -o ./publish
```

This produces a single executable `CallGraph` (or `CallGraph.exe` on Windows) in `./publish/`.
For Windows, use `-r win-x64` (or your target RID).

### 2. Install command shim + skills

Run from the publish folder:

```bash
./CallGraph install
```

On Windows:

```powershell
.\CallGraph.exe install
```

What `install` does:
- Deploys bundled `_claude`, `_codex`, `_cursor` only when matching target directories already exist in home (`~/.claude`, `~/.codex`, `~/.cursor`).
- Overwrites existing skill/agent/command files in those directories with the bundled versions.
- Does not auto-merge `AGENTS.md`/`CLAUDE.md`; prints manual instructions when template sections should be added.
- Configures Claude `PreToolUse` hook in `~/.claude/settings.json` (idempotent) to rewrite high-confidence C# shell searches to `callgraph` commands.
- Installs `callgraph` command shim:
  - macOS/Linux: removes duplicate `callgraph` symlinks on PATH (keeps the newly installed shim)
  - macOS/Linux: first writable directory already on `PATH` (fallback: `~/.local/bin/callgraph`)
  - Windows: `%LocalAppData%\Programs\callgraph\callgraph.exe`
- Updates Windows user `PATH` automatically (new shells).
- On macOS/Linux, if `~/.local/bin` is not on `PATH`, it prints the exact export command.

See [QUICKSTART.md](QUICKSTART.md) for more details.

## CLI Usage

### Lifecycle

```bash
# Index once
callgraph --index /path/to/solution.sln

# Reindex once
callgraph --reindex /path/to/solution.sln

# Index and keep watching
callgraph --index /path/to/solution.sln --watch

# Watch existing indexed solution (prompts if multiple)
callgraph --watch

# Clear index database
callgraph --clear
```

### Analysis and Search

```bash
# List indexed solutions
callgraph list-solutions   # auto-starts daemon on first call

# Search files
callgraph search-file --pattern "*Controller.cs" [--regex] [--solutionPath /path/to/solution.sln] [--solutionId <id>] [--folderPath /abs/folder] [--filePath /abs/file.cs]

# Search methods
callgraph search-method --keywords "login authentication" [--regex] [--pattern <pattern>] [--solutionPath /path/to/solution.sln] [--solutionId <id>] [--folderPath /abs/folder] [--filePath /abs/file.cs]

# Rewrite a shell command when a safe CallGraph equivalent exists
callgraph rewrite --command "find /abs/src -name \"*Controller.cs\""

# List methods (visibility defaults to external)
callgraph list-methods [--visibility external|internal] [--solutionPath /path/to/solution.sln] [--solutionId <id>] [--folderPath /abs/folder] [--filePath /abs/file.cs] [--fileList /abs/files.txt]

# Analyze call graph for file/method
callgraph analyze --filepath /abs/file.cs [--method MethodName] [--depth 1] [--direction inbound|outbound|bi-directional] [--visibility external|internal] [--solutionPath /path/to/solution.sln] [--solutionId <id>]

# Extract live method source from a known file
callgraph get-method-source --filePath /abs/file.cs [--methodName MethodName] [--containingType Namespace.Type] [--signature "Task Foo(string x)"] [--startLine 123] [--mode signature_only|signature_plus_body|body_only|body_without_comments]

# List unused diagnostics (file-scoped; required)
callgraph list-unused --projectPath /abs/project.csproj --filePath /abs/file.cs

# List warning diagnostics (file-scoped; required)
callgraph list-warnings --projectPath /abs/project.csproj --filePath /abs/file.cs
```

Notes:
- Analysis commands auto-start and reuse a background daemon by default.
- `rewrite` outputs a rewritten command when a safe equivalent exists; otherwise exits non-zero.
- Use `--no-daemon` for one-shot execution or `--require-daemon` to fail if daemon is unavailable.
- `analyze` defaults to depth `1` when `--depth` is omitted.
- `analyze` auto-selects the indexed solution when exactly one solution is indexed and no `--solutionPath`/`--solutionId` is provided.
- `search-file`, `search-method`, and `list-methods` return plain text lines (no JSON).
- `search-file`: one file path per line.
- `search-method`/`list-methods`: one match per line as tab-separated fields:
  `<filePath[:line]>\t<containingType>\t<methodName>\t<signature>`.
- `analyze`: plain text, line-based rows:
  - methods: `M\t<methodId>\t<filePath[:line]>\t<containingType>\t<methodName>`
  - calls: `C\t<callerMethodId>\t<calleeMethodId>\t<direction>`
- `get-method-source` returns structured JSON with the selected method content plus exact line/byte span.
- `get-method-source --mode` supports `signature_only`, `signature_plus_body` (default), `body_only`, and `body_without_comments`.
- `list-methods` defaults to `--visibility external` (public/protected/protected internal), and refreshes listed signatures from live source before output. Use `--visibility internal` to include all methods.
- `list-unused` and `list-warnings` require both `--projectPath` and `--filePath`.
- `--filePath` must be absolute and point to a `.cs` file.
- Diagnostic commands return structured raw JSON with `totalCount` and `diagnostics` (`diagnostics.length` is the returned count).

## Hybrid Method Search

`search-method` uses a hybrid loop for non-regex queries:

1. Lexical candidate fetch:
   - Split query into lexical tokens.
   - Expand a small synonym set (for example `login`/`signin`/`authentication`).
   - Query index with wildcard token patterns.
2. Lexical scoring:
   - Build method context from split namespace, containing class, method name, and signature.
   - Score candidate matches by weighted field overlap.
3. Top-K semantic rerank:
   - Keep lexical top-K candidates (default `200`).
   - Embed query and candidates with local `bge-small-en-v1.5`.
   - Re-rank by blended lexical + semantic score.
4. Return:
   - Return top results (default limit `200`) as line-based text output.

Regex searches (`--regex`) keep the previous regex-only search behavior.

Keyword matching is a weighted hybrid: candidate retrieval is effectively OR-based across query tokens, while lexical scoring boosts methods that match more keywords (closer to soft-AND ranking).

### Local bge-small-en-v1.5 Bundle

Model assets are expected in `models/bge-small-en-v1.5` and copied to output on build/publish.

Fetch a compatible local bundle:

```bash
./scripts/bootstrap-bge-small-en-v1.5.sh
```

Expected files:
- `model.onnx` or `model_quantized.onnx` (optionally under `onnx/`)
- `vocab.txt`

If model files are missing, CallGraph falls back to lexical-only ranking.

### Daemon Controls

```bash
# Start daemon manually (optional)
callgraph serve

# Check daemon status
callgraph status

# Stop daemon
callgraph stop
```

`serve` watches all indexed solutions at startup by default. Use `callgraph serve --no-watch-indexed` to disable watcher registration.
`serve` exits after 10 hours of inactivity by default; override with `callgraph serve --idleMinutes <n>`.

## Visibility Modes

Both modes traverse all edges (including private/internal methods):
- `external`: class-based depth (same-class calls do not increment depth)
- `internal`: method-based depth (every call increments depth)

## Configuration

Default index location:
- Windows: `%LocalAppData%\CallGraph\index.db`
- macOS: `~/Library/Application Support/CallGraph/index.db`

## SQLite Query Examples

The SQLite index is useful for exploratory queries, but treat these as heuristics:
- "potentially unused" from the index is not equivalent to Roslyn unused diagnostics; prefer `callgraph list-unused` for authoritative results
- `ContainingType` is stored as a minimally qualified type name, so same-named types from different namespaces can be grouped together

Use the SQLite CLI directly against the default index path:

```bash
# macOS
sqlite3 "$HOME/Library/Application Support/CallGraph/index.db"

# Windows
sqlite3 "%LocalAppData%\\CallGraph\\index.db"
```

Run a query inline and print column headers:

```bash
sqlite3 -header -column "$HOME/Library/Application Support/CallGraph/index.db" "
SELECT
  m.ContainingType,
  count(*) AS PublicMethodCount
FROM Methods m
WHERE lower(coalesce(m.Accessibility, '')) = 'public'
GROUP BY m.ContainingType
HAVING count(*) > 10
ORDER BY PublicMethodCount DESC;
"
```

```sql
-- Potentially unused private methods:
-- methods with no inbound edges inside the indexed call graph.
-- Excludes constructors, static constructors, and property accessors.
SELECT
  m.ContainingType,
  m.Display,
  m.FilePath,
  m.StartLine
FROM Methods m
LEFT JOIN Edges e
  ON e.SolutionId = m.SolutionId
 AND e.ToKey = m.Key
WHERE e.ToKey IS NULL
  AND lower(coalesce(m.Accessibility, '')) = 'private'
  AND lower(coalesce(m.Kind, '')) NOT IN (
    'constructor',
    'static-constructor',
    'property-get',
    'property-set'
  )
ORDER BY m.FilePath, m.StartLine;
```

```sql
-- Types with more than 10 public methods,
-- excluding common endpoint, adapter, and persistence naming patterns.
SELECT
  m.ContainingType,
  count(*) AS PublicMethodCount
FROM Methods m
WHERE lower(coalesce(m.Accessibility, '')) = 'public'
  AND coalesce(m.ContainingType, '') <> ''
  AND lower(coalesce(m.ContainingType, '')) NOT LIKE '%controller%'
  AND lower(coalesce(m.ContainingType, '')) NOT LIKE '%endpoint%'
  AND lower(coalesce(m.ContainingType, '')) NOT LIKE '%repository%'
  AND lower(coalesce(m.ContainingType, '')) NOT LIKE '%adapter%'
  AND lower(coalesce(m.ContainingType, '')) NOT LIKE '%dbcontext%'
  AND lower(coalesce(m.ContainingType, '')) NOT LIKE '%store%'
  AND lower(coalesce(m.ContainingType, '')) NOT LIKE '%dao%'
  AND lower(coalesce(m.FilePath, '')) NOT LIKE '%/controllers/%'
  AND lower(coalesce(m.FilePath, '')) NOT LIKE '%/endpoints/%'
  AND lower(coalesce(m.FilePath, '')) NOT LIKE '%/repositories/%'
  AND lower(coalesce(m.FilePath, '')) NOT LIKE '%/adapters/%'
  AND lower(coalesce(m.FilePath, '')) NOT LIKE '%/persistence/%'
GROUP BY m.ContainingType
HAVING count(*) > 10
ORDER BY PublicMethodCount DESC, m.ContainingType;
```

Override with configuration:

```json
{
  "IndexStore": {
    "DatabasePath": "D:\\path\\to\\index.db"
  },
  "MethodSearch": {
    "ResultLimit": 80,
    "LexicalTopK": 200,
    "MaxCandidatePool": 2000,
    "MaxPatternQueries": 8,
    "MinQueryTokenLength": 3,
    "EnableSemanticRerank": true,
    "SemanticWeight": 0.55
  },
  "SemanticSearch": {
    "BgeSmallEnV15": {
      "Enabled": true,
      "ModelDirectory": "models/bge-small-en-v1.5",
      "MaxSequenceLength": 128
    }
  }
}
```

## Behavior Notes

- Indexing is queued internally; CLI waits for completion unless `--watch` is active.
- Test projects are excluded from indexing/analysis.
- File watcher uses debounce for incremental reindexing.

## Testing

```bash
dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj
```
