---
name: callgraph-sql
description: Query the indexed CallGraph SQLite database directly with read-only SQL to find files, methods, types, and one-hop call relationships without scanning source.
---

# CallGraph SQL Query

## Command
`callgraph query "<SQL>"`

- Opens the index database **read-only**. Any write statement (INSERT/UPDATE/DELETE/DDL) is rejected.
- Output is **tab-separated**: the first line is a tab-joined header of column names, followed by one
  tab-separated row per result record. There is no JSON mode.
- Non-zero exit on SQL error, with the SQLite error message on stderr.
- If no index exists yet, the command errors and tells you to run `callgraph --index <sln>` first.

## Schema

### `Solutions`
| Column | Meaning |
|---|---|
| `Id` | Solution identifier |
| `Path` | Absolute path to the `.sln` (or project) that was indexed |
| `IndexedAtUtc` | Timestamp of the last successful index/reindex |
| `HeadCommit` | Git commit the index reflects |
| `SlnOnly` | Whether indexing targeted a single project vs a full solution |

### `Projects`
| Column | Meaning |
|---|---|
| `SolutionId` | FK to `Solutions.Id` |
| `Path` | Absolute path to the `.csproj` |
| `ReversePath` | Path segments reversed (fast suffix/basename matching) |

### `Files`
| Column | Meaning |
|---|---|
| `SolutionId` | FK to `Solutions.Id` |
| `Path` | Absolute source file path |
| `ReversePath` | Path segments reversed (fast suffix/basename matching) |
| `UpdatedAtUtc` | Last time this file's index entry was refreshed |

One row per indexed source file. Test-project files are excluded.

### `Methods`
| Column | Meaning |
|---|---|
| `Key` | Unique method identifier — used to join against `Edges` |
| `SolutionId` | FK to `Solutions.Id` |
| `FilePath` | Absolute file path containing the method |
| `Kind` | Member kind (method, constructor, property, etc.) |
| `Display` | Human-readable signature/name for display |
| `ContainingType` | Name of the declaring type |
| `StartLine` | 1-based line number where the member starts |
| `Accessibility` | public/private/internal/protected etc. |

One row per method/member. Use this table to find methods/types by name or containing type instead of scanning source.

### `Edges`
| Column | Meaning |
|---|---|
| `FromKey` | FK to `Methods.Key` — the caller |
| `ToKey` | FK to `Methods.Key` — the callee |
| `Direction` | Edge direction as stored (see worked examples below for join direction) |
| `Kind` | Call kind (e.g. direct call, invocation via interface, etc.) |
| `SolutionId` | FK to `Solutions.Id` |

One row per call relationship. Use this table for one-hop caller/callee lookups instead of scanning source.

### `SolutionAliases`
| Column | Meaning |
|---|---|
| `SolutionId` | FK to `Solutions.Id` |
| `AliasPath` | Alternate path the solution is also known by (e.g. worktree lineage) |

### `SolutionSnapshots`
| Column | Meaning |
|---|---|
| `SolutionId` | FK to `Solutions.Id` |
| `HeadCommit` | Commit this snapshot was captured at |
| `IndexedAtUtc` | When the snapshot was recorded |
| `PayloadJson` | Serialized snapshot payload used for incremental/commit-aware reindexing |

## Worked examples

Files by name:
```bash
callgraph query "SELECT Path FROM Files WHERE Path LIKE '%Controller.cs'"
```

Methods by name:
```bash
callgraph query "SELECT Display, FilePath, StartLine FROM Methods WHERE Display LIKE '%Login%'"
```

Methods in a type:
```bash
callgraph query "SELECT Display, StartLine FROM Methods WHERE ContainingType='FooService' ORDER BY StartLine"
```

One-hop callers of a method (who calls `<methodKey>`):
```bash
callgraph query "SELECT m.Display FROM Edges e JOIN Methods m ON m.Key = e.FromKey WHERE e.ToKey = '<methodKey>'"
```

One-hop callees of a method (what `<methodKey>` calls):
```bash
callgraph query "SELECT m.Display FROM Edges e JOIN Methods m ON m.Key = e.ToKey WHERE e.FromKey = '<methodKey>'"
```

To get a method's `Key` first, resolve it from `Methods` by `Display`/`ContainingType`/`FilePath`, then use that
value in the `Edges` join above.

After locating a method with `query`, read its exact body directly from the file at `FilePath:StartLine`
rather than trying to extract source via SQL — the index stores metadata, not method bodies.

## When not to use this

For multi-hop / recursive call-graph traversal (walking several hops of callers or callees, or building a
call tree), use `callgraph analyze` (see the `callgraph-analyze-callgraph` skill) instead of hand-written
recursive SQL. `analyze` already implements visibility-aware, depth-bounded traversal; reimplementing that
with recursive CTEs is unnecessary and error-prone.
