---
name: callgraph-sql
description: Query the indexed CallGraph SQLite database directly with read-only SQL for one-hop who-calls-whom relationships, consumer breadth/counts of a method or interface, and proving a member is unused — resolving interface/property/delegate-dispatched callers that text search (grep/ck refs on a type name) misses. Use for call-dependency questions, not for symbol existence/definition/line lookups (use ck) or string-only patterns like raw SQL.
---

# CallGraph SQL Query

## Command
`callgraph query "<SQL>"`

- Opens the index database **read-only**. Any write statement (INSERT/UPDATE/DELETE/DDL) is rejected.
- Output is **tab-separated**: the first line is a tab-joined header of column names, followed by one
  tab-separated row per result record. There is no JSON mode.
- Non-zero exit on SQL error, with the SQLite error message on stderr.
- If no index exists yet, the command errors and tells you to run `callgraph --index <sln>` first.

## When to use this (vs. `ck` / text search)

Reach for CallGraph when the question is **who calls whom** — it resolves the *semantic* caller/callee,
including calls dispatched through an interface, a property/accessor, or a delegate. Best fits:

- **Consumer breadth of an interface/method** — "how many places, and which modules, actually *call*
  `IFooComponent`?" Count `DISTINCT ContainingType` of callers; group by module.
- **Blast radius / reachability** — what breaks if I change/remove `X` (one-hop here; multi-hop via the
  `analyze` skill).
- **Proving a negative** — zero inbound edges means the member is genuinely unused (e.g. a "paper seam"
  interface nobody consumes yet). A real, trustworthy result.
- **Callers reached through indirection.** This is CallGraph's decisive advantage over grep: when code
  consumes an interface via a typed accessor (`context.Payments.GetAsync(...)` where `Payments` is typed
  `IPaymentSuperComponent`), the *source never writes the interface name*, so `grep`/`ck refs` on the
  interface name **undercounts**. CallGraph resolves the callee's declaring type and counts the call.
  Whenever a component is exposed via a DI/context accessor property, prefer CallGraph over text search.

Do **not** use CallGraph for: existence/definition/line-number/inheritance lookups (use `ck`
find-symbol / get-base-types); or for dependencies that produce **no C# call edge** — raw SQL (table
names in string literals), reflection, dynamic LINQ, DB-side procedures/views. Those are invisible to
the graph; find them with `ck`/`rg` text search and combine the two for a complete read census.

## Before you query

1. **Check the index is fresh and pick the right solution.** The DB may hold **more than one** indexed
   solution (e.g. a tool solution plus your app solution). Run this first and confirm `HeadCommit`
   matches your current `git rev-parse HEAD`:
   ```bash
   callgraph query "SELECT Id, Path, IndexedAtUtc, HeadCommit FROM Solutions"
   ```
   If more than one row exists, **filter every query** by that solution's `Id` (`WHERE e.SolutionId='…'`
   / `m.SolutionId='…'`) or by `FilePath LIKE '%/your-repo/%'` — otherwise callers/callees from an
   unrelated solution leak into results.
2. If `HeadCommit` is stale vs your working tree, reindex before trusting edges (`callgraph --reindex <sln>`).

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
| `Kind` | Member kind. Observed values: `method`, `constructor`, `static-constructor`, `property-get`, `property-set`, `local-function`, `operator`, `conversion-operator`, `event-add`. **Note:** properties appear as `property-get`/`property-set`, so a facade/accessor property (e.g. `Repositories.Payments`) is itself a `Methods` row you can find callers of. |
| `Display` | Human-readable signature/name for display |
| `ContainingType` | **Fully-qualified** declaring type (namespace + type), e.g. `Mews.Data.Entities.Repositories.Repositories`. For interface-dispatched edges the callee's `ContainingType` is the **interface** (e.g. `…IPaymentSuperComponent`), not the implementation. |
| `StartLine` | 1-based line number where the member starts |
| `Accessibility` | public/private/internal/protected etc. |

One row per method/member. Use this table to find methods/types by name or containing type instead of scanning source.

> **`LIKE` gotcha on `ContainingType`.** `LIKE '%IPaymentSuperComponent%'` matches the interface but not
> the implementation `PaymentSuperComponent` (the `I` prefix disambiguates) — convenient for isolating
> interface-dispatched calls. But watch for the reverse: `LIKE '%PaymentComponent%'` also matches
> `CreditCardPaymentComponent`, etc. Prefer exact `=` on the fully-qualified name when precision matters.

### `Edges`
| Column | Meaning |
|---|---|
| `FromKey` | FK to `Methods.Key` — the caller |
| `ToKey` | FK to `Methods.Key` — the callee |
| `Direction` | Edge direction as stored (see worked examples below for join direction) |
| `Kind` | Call kind. Observed values: `calls-direct`, `calls-via-interface`, `calls-via-property-get`, `calls-via-property-set`, `calls-via-delegate`, `calls-via-message`, `calls-via-event-add`, `calls-via-event-remove`. Use this to tell *how* a call is dispatched — e.g. `calls-via-property-get` is how facade/accessor-property reads show up; `calls-via-interface` is how DI/interface calls show up (the ones text search misses); `calls-via-delegate` is a method passed as a delegate/lambda. |
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

Consumers of a facade/accessor **property** (who reads `Repositories.PaymentProviders`) — a property is a
`property-get` method, so join by `Display`:
```bash
callgraph query "
SELECT DISTINCT caller.ContainingType, caller.FilePath
FROM Edges e
JOIN Methods callee ON callee.Key = e.ToKey
JOIN Methods caller ON caller.Key = e.FromKey
WHERE callee.Display = 'RichEntityRepository<PaymentProvider> Repositories.PaymentProviders.get'
ORDER BY caller.FilePath"
```

Consumer **breadth** of an interface, bucketed by module (effort estimation) — this is where CallGraph
beats grep, because it counts callers reaching the interface through a typed accessor:
```bash
callgraph query "
SELECT
  CASE
    WHEN caller.FilePath LIKE '%/Hosts/%' THEN 'Hosts'
    WHEN caller.FilePath LIKE '%/Mews.Business/%' THEN 'Core.Business'
    ELSE 'Other'
  END AS bucket,
  COUNT(DISTINCT caller.ContainingType) AS consumer_types
FROM Edges e
JOIN Methods callee ON callee.Key = e.ToKey
JOIN Methods caller ON caller.Key = e.FromKey
WHERE callee.ContainingType LIKE '%IPaymentSuperComponent%'
GROUP BY bucket ORDER BY consumer_types DESC"
```

Proving a member is unused ("paper seam") — expect an **empty** result set:
```bash
callgraph query "
SELECT caller.ContainingType, callee.Display
FROM Edges e
JOIN Methods callee ON callee.Key = e.ToKey
JOIN Methods caller ON caller.Key = e.FromKey
WHERE callee.ContainingType LIKE '%IPaymentCardStorage%'"
```

After locating a method with `query`, read its exact body directly from the file at `FilePath:StartLine`
rather than trying to extract source via SQL — the index stores metadata, not method bodies.

> **Validate surprising results by reading source.** CallGraph is a semantic index, not ground truth.
> When a count contradicts a text search (usually because access is via an accessor property the grep
> couldn't see), resolve it by opening the caller file at `FilePath:StartLine` and confirming the call —
> don't trust either tool blindly. In practice CallGraph wins these on interface/accessor dispatch, and
> text search wins on string-literal/raw-SQL usage.

## When not to use this

For multi-hop / recursive call-graph traversal (walking several hops of callers or callees, or building a
call tree), use `callgraph analyze` (see the `callgraph-analyze-callgraph` skill) instead of hand-written
recursive SQL. `analyze` already implements visibility-aware, depth-bounded traversal; reimplementing that
with recursive CTEs is unnecessary and error-prone.
