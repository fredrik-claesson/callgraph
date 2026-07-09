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

Put it on your `PATH` as `callgraph`. Prefer a **symlink** so it stays current when you re-publish:

```bash
ln -sf "$(pwd)/publish/CallGraph" ~/.local/bin/callgraph   # ~/.local/bin must be on PATH
```

(A plain copy goes stale — after the next `dotnet publish` you'd still be running the old binary. Verify
what you're running with `callgraph --help`; the usage should list `--index`, `--reindex`, `--clear`,
`query`, and `analyze`.)

Verify:

```bash
./publish/CallGraph --help
```

## Use it on a code repository

CallGraph works against a repository's **solution file** (`.sln`). The index is stored globally
(see [Configuration](#configuration)), keyed by the solution's absolute path — so you run the CLI
from anywhere, and you can index the solutions of several repositories side by side.

### 1. Find the solution to index

```bash
cd /path/to/your-repo
find . -name '*.sln'          # locate the solution(s) in the repo
```

If a repository has more than one `.sln`, index each one you care about (step 2). Test projects are
skipped automatically during indexing.

### 2. Index the repository's solution

```bash
callgraph --index "$(pwd)/src/YourApp.sln"
```

This loads the solution with Roslyn and builds the SQLite index at the default location (see
[Configuration](#configuration) below). Indexing is required before `query` or `analyze` will work.
For a large repository the first index can take a while; progress is logged and the CLI waits for
completion.

### 3. Explore the code with `query` and `analyze`

Now navigate the codebase without opening files (examples in the two sections below). When the repo
changes, keep the index current with `--reindex` (next section).

## Keep the index current

```bash
# Reindex the same solution (full re-index)
callgraph --reindex

# Reindex a specific solution
callgraph --reindex /abs/path/to/solution.sln
```

## Query the index

Common repository-navigation tasks, expressed as SQL over the index:

```bash
# Find files by name (e.g. all controllers)
callgraph query "SELECT Path FROM Files WHERE Path LIKE '%Controller.cs'"

# Find a method by name across the whole repo
callgraph query "SELECT Display, FilePath, StartLine FROM Methods WHERE Display LIKE '%Login%'"

# List every method declared in one type
callgraph query "SELECT Display, StartLine FROM Methods WHERE ContainingType LIKE '%OrderService' ORDER BY StartLine"

# Direct callers of a method (one hop), via the Edges table joined back to Methods
callgraph query "SELECT c.Display, c.FilePath, c.StartLine FROM Edges e JOIN Methods c ON c.Key = e.FromKey WHERE e.ToKey = '<methodKey>'"
```

- `query` runs read-only SQL against the index; write statements are rejected with a non-zero exit.
- Output is tab-separated: a header line of column names, then one row per result.
- The index is one database across all indexed solutions. If you have indexed more than one repository,
  filter by solution — e.g. `... WHERE SolutionId = (SELECT Id FROM Solutions WHERE Path = '/abs/YourApp.sln')`.
- `<methodKey>` is the `Methods.Key` value (the canonical symbol identity); get it from a first query, e.g.
  `SELECT Key FROM Methods WHERE Display LIKE '%PlaceOrder%'`.
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

## Make the skills available to your coding agent

CallGraph ships two Claude skills under `_claude/skills/` so an agent working in any repository knows
how to drive it:

- **`callgraph-sql`** — the DB schema and worked `query` examples.
- **`callgraph-analyze-callgraph`** — how to trace call graphs with `analyze`.

Install them once into your user-level Claude config so they apply across all your repositories:

```bash
./deploy.sh
```

This copies the two skills into `~/.claude/skills/` (it does **not** touch your existing
`~/.claude/CLAUDE.md`). Run it as yourself — not with `sudo` — since it writes into your home directory.
After that, Claude Code picks up the skills automatically whenever you work in a C# repo whose solution
you have indexed — no per-repo setup needed. (The `callgraph` executable must be on your `PATH`; see
[Build and publish](#build-and-publish).)

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
