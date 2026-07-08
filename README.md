# CallGraph CLI

CallGraph is a local-first .NET CLI that indexes a C# solution with Roslyn into a SQLite database, giving coding agents and developers fast, compiler-accurate SQL lookups and call graph analysis — without scanning source files in prompts.

> **Indexing is required.** All analysis commands operate against a local index. Run `callgraph --index /path/to/solution.sln` once to bootstrap. After that, reindex on demand with `callgraph --reindex` (git-aware and incremental). See [QUICKSTART.md](QUICKSTART.md) to get up and running.

## Features

### Token Savings

| Feature | Data source | How it saves tokens |
|---|---|---|
| **`query "<SQL>"`** | Indexed | Read-only SQL against files/methods/edges — compact tab-separated rows instead of directory scans or full-file reads to locate symbols |
| **`analyze` with `--depth`, `--direction`, `--visibility`** | Indexed | Limits call-graph traversal to what's relevant — agents don't receive the full call tree |
| **Solution scoping (`--solutionPath`/`--solutionId`)** | N/A | Narrows `analyze` to a specific indexed solution, eliminating cross-solution noise |
| **Compact tab-delimited output** | N/A | `query` prints a header line plus tab-separated rows; `analyze` prints `M`/`C` rows — both far more compact than equivalent JSON |
| **Bundled skills in `_claude/skills/`** | N/A | `callgraph-sql` and `callgraph-analyze-callgraph` document schema and usage patterns, reducing the agent's need to reason out flags/queries from scratch |

### Improved Quality and Precision

| Feature | Data source | Precision benefit |
|---|---|---|
| **Roslyn-based indexing** | Live AST (at index time) | Compiler-accurate symbol resolution — no false positives from text matching (e.g. method name collisions across types) |
| **Method-level call graph with edge kinds** | Indexed | Precise caller/callee relationships at method granularity, not class or file level |
| **Inbound / outbound / bi-directional analysis** | Indexed | Blast-radius analysis (inbound) vs. dependency tracing (outbound) without conflation |
| **File path + line numbers on all results** | Indexed | Every result is directly navigable — no ambiguity about which overload or which file |
| **Git-aware incremental reindex** | Indexed | Restores from snapshots when switching to a previously-indexed commit; otherwise uses git diff to detect changed files and reprocesses only those — index stays synchronized without a full rescan |
| **Test project exclusion** | N/A (policy applied at index time) | Skips test projects entirely during indexing — reduces index size and reindex time |
| **SQLite-backed index store** | Indexed | Persistent on-disk index survives restarts without re-indexing the solution |

## Build

```bash
dotnet build ./CallGraph.csproj -c Release
```

Or publish a single executable:

```bash
dotnet publish ./CallGraph.csproj -c Release -r osx-arm64 -o ./publish
```

This produces a single executable `CallGraph` (or `CallGraph.exe` on Windows) in `./publish/`.
For Windows, use `-r win-x64` (or your target RID).

See [QUICKSTART.md](QUICKSTART.md) for a full walkthrough.

## CLI Usage

```bash
# Index once
callgraph --index /path/to/solution.sln

# Reindex (git-aware, incremental)
callgraph --reindex [/path/to/solution.sln]

# Clear the index database
callgraph --clear

# Run read-only SQL against the index
callgraph query "SELECT Path FROM Files WHERE Path LIKE '%Controller.cs'"

# Analyze call graph for a file/method
callgraph analyze --filepath /abs/file.cs [--method MethodName] [--depth 1] [--direction inbound|outbound|bi-directional] [--visibility external|internal] [--solutionPath /path/to/solution.sln] [--solutionId <id>]
```

Notes:
- `--reindex` with no path reindexes the current (or only) indexed solution; otherwise it targets the given `.sln`.
- `query` opens the index database **read-only**; write statements (INSERT/UPDATE/DELETE/DDL) are rejected with a non-zero exit and an error on stderr.
- `query` output is tab-separated: a header line of column names, then one tab-separated row per result.
- `analyze` defaults to depth `1`, direction `bi-directional`, and visibility `external` when the corresponding flags are omitted.
- `analyze` auto-selects the indexed solution when exactly one solution is indexed and no `--solutionPath`/`--solutionId` is provided.
- `analyze` output is plain text, line-based:
  - methods: `M\t<methodId>\t<filePath[:line]>\t<containingType>\t<methodName>`
  - calls: `C\t<callerMethodId>\t<calleeMethodId>\t<direction>`

## Bundled Skills

Two Claude skills ship under `_claude/skills/`:

- **`callgraph-sql`** — documents the full DB schema and worked `query` examples (find files/methods/types, one-hop callers/callees).
- **`callgraph-analyze-callgraph`** — documents `analyze` usage for multi-hop call-graph traversal.

## Visibility Modes

Both modes traverse all edges (including private/internal methods):
- `external`: class-based depth (same-class calls do not increment depth)
- `internal`: method-based depth (every call increments depth)

## Configuration

Default index location:
- Windows: `%LocalAppData%\CallGraph\index.db`
- macOS: `~/Library/Application Support/CallGraph/index.db`

Override with configuration:

```json
{
  "IndexStore": {
    "DatabasePath": "D:\\path\\to\\index.db"
  }
}
```

## Database Schema

The index is a SQLite database with 7 tables:

- `Solutions(Id, Path, IndexedAtUtc, HeadCommit, SlnOnly)`
- `Projects(SolutionId, Path, ReversePath)`
- `Files(SolutionId, Path, ReversePath, UpdatedAtUtc)`
- `Methods(Key, SolutionId, FilePath, Kind, Display, ContainingType, StartLine, Accessibility)`
- `Edges(FromKey, ToKey, Direction, Kind, SolutionId)`
- `SolutionAliases(SolutionId, AliasPath)`
- `SolutionSnapshots(SolutionId, HeadCommit, IndexedAtUtc, PayloadJson)`

See the `callgraph-sql` skill for column-by-column documentation and worked query examples, including:

```sql
-- Potentially unused private methods:
-- methods with no inbound edges inside the indexed call graph.
-- Excludes constructors, static constructors, and property accessors.
-- Include m.Key when reviewing results so overloads are distinguishable.
SELECT
  m.ContainingType,
  m.Display,
  m.Key,
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

`m.Key` is the canonical symbol identity in the index. Prefer it over `m.Display`
when comparing methods, because overloads can share the same display name but
have different parameter types and different callers.

Treat "potentially unused" results as a heuristic, not ground truth — the index has no
reflection/interface-implementation awareness that a compiler diagnostic would have.

## Behavior Notes

- Indexing is queued internally; the CLI waits for completion.
- Test projects are excluded from indexing/analysis.
- `--reindex` is git-aware and incremental: it first tries to restore from a saved snapshot if the current HEAD commit has been indexed before; otherwise, it computes changed files via git diff between the last-indexed commit and current HEAD and reprocesses only those. When git info is unavailable, it falls back to timestamp-based incremental reindexing. Full reindex is the last resort when changes exceed a threshold.

## Testing

```bash
dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj
```
