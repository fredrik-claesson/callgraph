---
name: callgraph-sql
description: Query the indexed CallGraph SQLite database directly with read-only SQL for one-hop who-calls-whom relationships, consumer breadth/counts of a method or interface, and proving a member is unused — resolving interface/property/delegate-dispatched callers that text search (grep/ck refs on a type name) misses. Use for call-dependency questions, not for symbol existence/definition/line lookups (use ck) or string-only patterns like raw SQL.
---

# CallGraph SQL Query

## Command
`callgraph query "<SQL>"`

- Read-only; writes (INSERT/UPDATE/DELETE/DDL) are rejected.
- Output is **tab-separated** — header row, then one row per record. No JSON mode.
- Non-zero exit on SQL error (message on stderr). If no index exists, it tells you to run
  `callgraph --index <sln>`.

## When to use this (vs. `ck` / text search)

The question is **who calls whom**, and CallGraph resolves the *semantic* callee — including calls
dispatched via interface, property/accessor, or delegate. Best fits: consumer breadth of an
interface, one-hop blast radius, and **proving a negative** (zero inbound edges = genuinely unused).

**The decisive advantage over grep** is indirection: when code reaches a component through a typed
accessor (`context.Payments.GetAsync(...)` where `Payments` is typed `IPaymentSuperComponent`), the
source never writes the interface name, so grep/`ck refs` undercounts. Prefer CallGraph whenever a
component is exposed via a DI/context accessor property.

Do **not** use it for symbol existence/definition/inheritance (use `ck`), or for dependencies with
**no C# call edge** — raw SQL in string literals, reflection, dynamic LINQ, DB-side procedures. Those
are invisible to the graph; find them with text search and combine.

## Before you query

Confirm freshness and solution scope in one step — the DB may hold **more than one** indexed solution:

```bash
callgraph query "SELECT Id, Path, IndexedAtUtc, HeadCommit FROM Solutions"
```

If `HeadCommit` is stale vs `git rev-parse HEAD`, reindex (`callgraph --reindex <sln>`) before
trusting edges. If more than one row exists, **filter every query** by `SolutionId` — otherwise
results from an unrelated solution leak in.

## Schema

### `Solutions`
`Id` · `Path` (absolute `.sln`/project path) · `IndexedAtUtc` · `HeadCommit` (commit the index
reflects) · `SlnOnly` (single project vs full solution).

### `Projects` / `Files`
Both: `SolutionId`, `Path`, `ReversePath` (segments reversed, for fast suffix/basename matching).
`Files` also has `UpdatedAtUtc`. One row per `.csproj` / per indexed source file.
**Test-project files are excluded from the index.**

### `SolutionAliases`
`SolutionId` + `AliasPath` — alternate paths a solution is known by (e.g. worktree lineage).

### `Methods`
| Column | Meaning |
|---|---|
| `Key` | Unique method identifier — join target for `Edges` |
| `SolutionId` | FK to `Solutions.Id` |
| `FilePath` | Absolute file path containing the member |
| `Kind` | `method`, `constructor`, `static-constructor`, `property-get`, `property-set`, `local-function`, `operator`, `conversion-operator`, `event-add`. **Properties appear as `property-get`/`property-set`**, so a facade accessor (`Repositories.Payments`) is itself a row you can find callers of. |
| `Display` | Human-readable signature |
| `ContainingType` | **Fully-qualified** declaring type. For interface-dispatched calls the `calls-direct` callee is the **interface**; a parallel `calls-via-interface` edge points at the concrete **implementation**. Match the interface for consumer-surface questions, the implementation for "which code runs". |
| `StartLine` | 1-based start line |
| `Accessibility` | public/private/internal/protected |

> **`LIKE` gotcha on `ContainingType`.** `'%IPaymentSuperComponent%'` matches the interface but not
> the impl `PaymentSuperComponent` — convenient for isolating interface calls. The reverse bites:
> `'%PaymentComponent%'` also matches `CreditCardPaymentComponent`. Prefer exact `=` when precision
> matters.

### `Edges`
| Column | Meaning |
|---|---|
| `FromKey` | FK to `Methods.Key` — the **caller** |
| `ToKey` | FK to `Methods.Key` — the **callee** |
| `Direction` | **Constant — every row is `outbound`.** Don't filter on it or infer from it; join direction determines caller vs callee. |
| `Kind` | How the call dispatches — see below |
| `SolutionId` | FK to `Solutions.Id` |

**`Kind` values and what they mean:**

- `calls-direct` / `calls-via-interface` — **dual-edge rule:** a call on an interface-typed reference
  records **two** edges: `calls-direct` to the *interface* method (syntactic target) **and**
  `calls-via-interface` to each concrete *implementation*. Query the interface for **consumer
  surface** (the set text search misses); follow `calls-via-interface` for **which impl runs**.
- `calls-via-property-get` / `-set` — facade/accessor-property reads and writes.
- `calls-via-delegate` — method passed as a delegate/lambda.
- `calls-via-event-add` / `-remove` / `-handler` — event subscription plumbing.
- `calls-via-message` — **not a call.** See the callout below before using it anywhere.

> **⚠️ `calls-via-message` is a *may-dispatch* edge — exclude it from aggregate queries.**
> A dispatch site can't be statically resolved to one handler, so the index records an edge to
> **every method of every handler-shaped type in the solution**. These are possibilities, not calls.
> Observed on a large monolith (~632k edges): ~8% of all edges, from 985 source methods, averaging
> **~49 targets each and peaking at 296**. One command handler's `Handle` linked to 51 targets across
> 24 unrelated types and called none of them.
>
> **Default rule: add `AND e.Kind <> 'calls-via-message'` to anything that counts, groups, aggregates
> or classifies.** Omitting it silently inflates results and never errors.
>
> Query `Kind = 'calls-via-message'` **on its own** for the one question it answers well: *which
> handlers could process this message?*
>
> This one bites harder than the others because grep gives no warning — it finds nothing, so the
> phantom edge reads as exactly the indirection CallGraph is supposed to catch and grep cannot. Always
> confirm a cross-module message edge at `FilePath:StartLine` before reporting it.

## Worked examples

Resolve a method's `Key` from `Methods` (by `Display`/`ContainingType`/`FilePath`), then:

```bash
# callers of a method            (callees: swap FromKey/ToKey)
callgraph query "SELECT m.Display FROM Edges e JOIN Methods m ON m.Key=e.FromKey WHERE e.ToKey='<key>'"
```

Consumers of a facade/accessor **property** — a property is a `property-get` method, so join on `Display`:
```bash
callgraph query "
SELECT DISTINCT caller.ContainingType, caller.FilePath
FROM Edges e
JOIN Methods callee ON callee.Key = e.ToKey
JOIN Methods caller ON caller.Key = e.FromKey
WHERE callee.Display = 'RichEntityRepository<PaymentProvider> Repositories.PaymentProviders.get'"
```

Consumer **breadth** bucketed by module (effort estimation) — CallGraph beats grep here because it
counts callers reaching the interface through a typed accessor:
```bash
callgraph query "
SELECT
  CASE WHEN caller.FilePath LIKE '%/Hosts/%' THEN 'Hosts'
       WHEN caller.FilePath LIKE '%/Mews.Business/%' THEN 'Core.Business'
       ELSE 'Other' END AS bucket,
  COUNT(DISTINCT caller.ContainingType) AS consumer_types
FROM Edges e
JOIN Methods callee ON callee.Key = e.ToKey
JOIN Methods caller ON caller.Key = e.FromKey
WHERE callee.ContainingType LIKE '%IPaymentSuperComponent%' AND e.Kind <> 'calls-via-message'
GROUP BY bucket ORDER BY consumer_types DESC"
```

Proving a member is unused ("paper seam") — expect an **empty** result:
```bash
callgraph query "
SELECT caller.ContainingType FROM Edges e
JOIN Methods callee ON callee.Key = e.ToKey
JOIN Methods caller ON caller.Key = e.FromKey
WHERE callee.ContainingType LIKE '%IPaymentCardStorage%'"
```

## Verifying results

The index stores metadata, not bodies — read source at `FilePath:StartLine` to confirm anything
surprising. Two failure modes specifically:

- **Name patterns on `Display` match unintended semantics.** `Display` is a signature string, not a
  typed effect. When classifying by verb (a common shortcut for "does this slice write data?"),
  `'%.Delete%'` matches an HTTP `DeleteAccount` on a REST client, `'%.Create(%'` matches value-object
  factories, `'%.Set%'` matches every `Settlement…` member. Constrain by `ContainingType` too.
- **Counts contradicting text search.** Usually CallGraph is right (accessor dispatch grep can't see),
  but confirm rather than trusting either blindly. Text search wins on string-literal/raw-SQL usage.

## When not to use this

For multi-hop traversal or call trees, use `callgraph analyze` (the `callgraph-analyze` skill) — it
implements visibility-aware, depth-bounded traversal; hand-rolled recursive CTEs are error-prone.

**One exception where SQL is right:** classifying many slices against a fixed sink set ("which of
these 12 folders touch persistence at all?"). That's a one-hop membership test repeated over slices,
not a traversal — and see the reachability-saturation note in `callgraph-analyze` for why the
multi-hop framing answers that question vacuously.
