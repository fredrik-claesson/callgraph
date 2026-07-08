# Slim CallGraph to index / reindex / query / analyze

**Date:** 2026-07-08
**Status:** Approved (brainstorming)

## Goal

Reduce CallGraph from a broad multi-command tool (with a background daemon,
file-system watcher, bundled multi-agent skill packs, semantic search, and a
dozen analysis subcommands) to a slim tool with a single purpose: **build a
queryable SQLite index of a C# solution, and let agents query it.**

The CLI keeps only what cannot be expressed as SQL:

- **indexing** (`--index` / `--reindex` / `--clear`), and
- **recursive call-graph traversal** (`analyze`), which is fragile and
  error-prone to express as raw recursive SQL and already has working C#.

Everything else (find files, find methods, list methods in a type, find
callers/callees one hop) becomes a documented SQL query against the index.

## Final CLI surface

| Command | Status | Notes |
|---|---|---|
| `callgraph --index <sln>` | keep | unchanged |
| `callgraph --reindex [sln]` | keep | retains git-aware incremental reindex + commit snapshots |
| `callgraph --clear` | keep | cheap index reset; not a watcher or a skill |
| `callgraph query "<SQL>"` | **new** | read-only SQLite connection; tab-separated output with a header row |
| `callgraph analyze --filepath <file.cs> [--method <name>] [--depth <n>] [--direction <inbound\|outbound\|bi-directional>] [--visibility <external\|internal>] [--solutionPath <path>] [--solutionId <id>]` | keep | one-shot, local (reads the indexed DB); no daemon |

### Removed commands

`install`, `rewrite`, `list-solutions`, `search-file`, `search-method`,
`list-methods`, `get-method-source`, `list-unused`, `list-warnings`, `serve`,
`status`, `stop`, and the `--watch` flag.

## `callgraph query` behavior

- Opens the index DB **read-only** (SQLite `Mode=ReadOnly`). Write statements
  fail rather than mutate the index.
- DB path resolved internally (same resolution as indexing: configured
  `DatabasePath`, else `<LocalApplicationData>/CallGraph/index.db`).
- Output: **tab-separated**, first line is the column header, one row per
  result record. No JSON mode.
- Non-zero exit on SQL error, with the SQLite error message on stderr.

## Code removed

- `Core/Watching/*` — file-system watcher.
- Daemon: `Cli/Bootstrap/DaemonCommandRunner`, `Cli/DaemonProtocol`, and daemon
  dispatch in `Program.cs`.
- `Core/Search/*` + the ONNX runtime package reference, `models/`, and
  `scripts/bootstrap-bge-small-en-v1.5.sh`. Semantic search was query-time only;
  no embeddings are stored in the DB, so removing it does not change the index.
- `Core/Diagnostics/*` (warnings / unused) and `Core/Extraction/*`
  (get-method-source).
- `Cli/InstallCommandRunner`, `Cli/CommandRewriteEngine`, and
  `Cli/ToolCommandExecutor` (replaced by a slim executor for `query` + `analyze`).
- Contracts referenced only by removed commands (search / diagnostics / tool
  responses).
- Bundled agent-config directories: `_codex/`, `_cursor/`, `_copilot/`,
  `_opencode/`.

## Code kept

- `Core/Indexing/*`, `Core/Projects/*`, `Core/Solutions/*`, `Core/Git/*` — the
  indexing pipeline and git-aware incremental reindex.
- `Core/Analysis/*` — graph resolution for `analyze`, plus the minimal output
  formatting `analyze` needs.
- A new small **read-only query executor** for `callgraph query`.

## Database schema (documented by the query skill)

The index is a SQLite DB with these tables (unchanged by this work):

- `Solutions(Id, Path, IndexedAtUtc, HeadCommit, SlnOnly)`
- `Projects(SolutionId, Path, ReversePath)`
- `Files(SolutionId, Path, ReversePath, UpdatedAtUtc)`
- `Methods(Key, SolutionId, FilePath, Kind, Display, ContainingType, StartLine, Accessibility)`
- `Edges(FromKey, ToKey, Direction, Kind, SolutionId)`
- `SolutionAliases(SolutionId, AliasPath)`
- `SolutionSnapshots(SolutionId, HeadCommit, IndexedAtUtc, PayloadJson)`

## Skills (repo file only — no install)

Two skills survive, under `_claude/skills/`:

### `callgraph-sql` (new)

Documents:
- the full schema above (tables + columns + what a row means),
- the `callgraph query` command and its tab-separated, read-only output contract,
- worked example queries that replace the removed commands:
  - find files by name pattern (`Files`),
  - find methods by name / containing type (`Methods`),
  - list methods in a type (`Methods` filtered by `ContainingType`),
  - one-hop callers / callees (`Edges` joined to `Methods`),
- guidance to use `callgraph analyze` (not raw recursive SQL) for multi-hop
  call-graph traversal.

### `callgraph-analyze-callgraph` (rewritten)

- Discovery steps switch from the removed commands to `callgraph query` (SQL)
  and direct file reads.
- The daemon / `--no-daemon` retry policy is removed.
- Frontmatter drops `agent: callgraph-haiku` and `context: fork`; the skill runs
  inline in the main context.

## Removed agent-config artifacts

- `_claude/CLAUDE.md` (assumes removed commands + daemon) — reduced to a minimal
  note pointing at the two surviving skills (`callgraph-sql`,
  `callgraph-analyze-callgraph`), with the daemon/rewrite/discovery policy removed.
- `_claude/hooks/callgraph-rewrite.sh` PreToolUse hook (rewrites shell searches
  to removed commands) — removed.
- `_claude/agents/callgraph-haiku.md` — removed.
- The remaining `_claude/skills/*` (list-methods, search-file, search-method,
  get-method-source, list-unused, list-warnings, sequence-diagram,
  pr2-deep-pr-review) — removed.

## Documentation updates

- `README.md`, `QUICKSTART.md`, and `CLAUDE.md` (project root) updated to
  describe only `--index` / `--reindex` / `--clear` / `query` / `analyze`, the DB
  schema, and the two skills.

## Non-goals

- No change to the indexing pipeline's output or the DB schema.
- No new query features beyond passthrough SQL (no saved queries, no query DSL).
- No change to git-aware incremental reindex behavior.

## Verification

- Build the trimmed project; confirm it compiles with the removed namespaces and
  package references gone.
- `--index` a sample solution, then confirm `query` returns rows from each table
  and rejects a write statement.
- Run `analyze` on an indexed file one-shot (no daemon) and confirm output.
- Confirm the two skills reference only surviving commands.
