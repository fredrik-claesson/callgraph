# QUICKSTART

Get CallGraph running end-to-end.

## Requirements

- .NET SDK (repo targets `net10.0`)

## Build and publish

```bash
dotnet publish ./CallGraph.csproj -c Release -r osx-arm64 -o ./publish
```

This produces a single executable `CallGraph` (or `CallGraph.exe` on Windows) in `./publish/`.
For Windows, use `-r win-x64` (or your target RID).

Run it directly from `./publish/`, or copy/symlink it onto your `PATH` as `callgraph`.

Verify:

```bash
./publish/CallGraph --help
```

## Index a solution

```bash
callgraph --index /abs/path/to/solution.sln
```

This builds the SQLite index at the default location (see [Configuration](#configuration) below). Indexing
is required before `query` or `analyze` will work.

## Keep the index current

```bash
# Reindex the same solution (git-aware, incremental)
callgraph --reindex

# Reindex a specific solution
callgraph --reindex /abs/path/to/solution.sln
```

## Query the index

```bash
callgraph query "SELECT Path FROM Files WHERE Path LIKE '%Controller.cs'"
callgraph query "SELECT Display, FilePath, StartLine FROM Methods WHERE Display LIKE '%Login%'"
```

- `query` runs read-only SQL against the index; write statements are rejected.
- Output is tab-separated: a header line of column names, then one row per result.
- See the `callgraph-sql` skill (`_claude/skills/callgraph-sql/SKILL.md`) for the full schema and more worked examples.

## Analyze a call graph

```bash
callgraph analyze --filepath "/abs/path/to/File.cs" --depth 1 --direction bi-directional --visibility external
```

- Defaults: `--depth 1`, `--direction bi-directional`, `--visibility external`.
- Auto-selects the indexed solution when exactly one solution is indexed; otherwise pass `--solutionPath` or `--solutionId`.
- See the `callgraph-analyze-callgraph` skill (`_claude/skills/callgraph-analyze-callgraph/SKILL.md`) for usage guidance.

Output notes:
- `analyze`: plain text rows
  - `M\t<methodId>\t<filePath[:line]>\t<containingType>\t<methodName>`
  - `C\t<callerMethodId>\t<calleeMethodId>\t<direction>`

## Clear the index

```bash
callgraph --clear
```

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

## Notes

- Test projects are excluded from indexing/analysis.
