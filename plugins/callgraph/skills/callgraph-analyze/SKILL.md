---
name: callgraph-analyze
description: Run multi-hop CallGraph traversal for a C# file/method via CLI to map inbound/outbound calls — for blast-radius and hot-path reachability ("what breaks if I change this", "does A reach B"), following interface/property/delegate dispatch that text search misses. For one-hop caller/callee lists or consumer counts use callgraph-sql; for symbol existence/definition use ck.
---

# C# Analyze Call Graph

## When to use this
Use `analyze` when the question needs **multi-hop traversal** of the call graph:
- **Blast radius / hot-path reachability** — "if I change or remove this method, what upstream flows
  break?" An inbound trace surfaces the real entry points (controllers, jobs, event handlers) and shows
  whether a change reaches a *charge/payment hot path* vs. only a read screen.
- **Reachability between two areas** — does anything under module A eventually call into method B?
- **Following a value/effect across layers** — narrow candidate hops, then confirm the sink by reading source.

`analyze` traverses **all** edge kinds, including interface, property/accessor, and delegate dispatch — so
it finds callers that reach a component through a typed accessor (`context.Foo.Bar()`), which text search
(`grep`/`ck refs` on the type name) structurally cannot see. That indirection-awareness is the main reason
to prefer it over source scanning for reachability questions.

> **Reachability saturates against infrastructure targets.** "Does A reach B" is only meaningful when
> B is *specific*. If B is something most code funnels into — `SaveChanges`, a DbContext member, a
> logger, a transaction scope — then in a large codebase essentially every A reaches it, and an
> unbounded yes carries no information. **Symptom: every candidate you classify comes back positive.**
> That means the target is too generic, not that the codebase is uniformly coupled. Either bound the
> traversal to the slice itself (does A reach B *through A's own code*, vs. only via a component that
> would become a service boundary — a data dependency vs. a port dependency), or drop to a one-hop
> membership test against a sink set in SQL (`callgraph-sql`).

Prefer the **`callgraph-sql`** skill instead when you only need **one-hop** caller/callee lists or
**counts/breadth** (e.g. "how many modules call `IFooComponent`") — that's a single `Edges` join, no
traversal. And note the shared blind spot of both tools: dependencies with **no C# call edge** — raw SQL
(table names in strings), reflection, dynamic LINQ, DB-side procedures — are invisible to the graph; find
those with `ck`/`rg` text search and combine.

## Inputs
Parse the user request for:
- filepath (required)
- --method <name> (optional, case-sensitive)
- --depth <n> (optional, default 1)
- --direction inbound|outbound|bi-directional (optional, default bi-directional)
- --visibility external|internal (optional, default external)
- --solutionPath / --solutionId (optional, for disambiguation)

## Scope rule
- CallGraph index scope excludes test projects and the source files in those test projects.
- If the target file is known, run `callgraph analyze` directly on that file.
- Do not perform broader discovery first unless the user explicitly asks for it.
- If the user provides a class + method but no file path, resolve the file first with:
  `callgraph query "SELECT Path FROM Files WHERE Path LIKE '%<ClassName>.cs'"`
- When identifying candidate methods in a known file/type, prefer scoped discovery via `callgraph query` against
  the `Methods` table (filter by `FilePath` or `ContainingType`), then read the exact source with a direct file
  read at the reported `StartLine`. See the `callgraph-sql` skill for schema and worked query examples.
- Use `analyze` to find relationships and candidate hops, not to infer detailed filter/query behavior by itself.
- If the user is asking why data changes, which filter wins, or where a query is shaped, use `analyze` to narrow
  candidates, then inspect the downstream implementation with a direct file read until the real sink is found.

## Visibility (depth strategy)
- `external`: Class-based depth. Same-class calls don't increment depth. Use for component-level analysis.
- `internal`: Method-based depth. Every call increments depth. Use for detailed analysis.
- If a concrete method is known, prefer starting with `--visibility internal --depth 1` to keep output focused, then widen only if needed.
- Safety cap: when using `internal`, depth must be `<= 2`.
- If deeper tracing is needed, use two-stage analysis:
  1. map callers first with `--direction inbound --visibility external --depth 2`,
  2. pick 1-3 candidates and run `--direction outbound --visibility internal --depth 2` per candidate.

Both modes traverse ALL edges including private/internal methods.

## Action
Run CLI:
`callgraph analyze --filepath <file.cs> [--method <name>] [--depth <n>] [--direction <value>] [--visibility <value>] [--solutionPath <path>] [--solutionId <id>]`

## Error handling
- **AmbiguousSolution**: Rerun with solutionPath or solutionId
- **TargetsNotFound**: Suggest providing --method or verify filepath
- **IndexNotReady**: Instruct user to run CLI with `--index` or `--reindex`
- If solutionPath/solutionId is omitted and exactly one indexed solution exists, the CLI auto-selects that solution.

## Output
Summarize node/edge counts and key inbound/outbound calls.

## Response format (line-based)
- Method rows:
  - `M\t<methodId>\t<filePath[:line]>\t<containingType>\t<methodName>`
- Call rows:
  - `C\t<callerMethodId>\t<calleeMethodId>\t<direction>`

Use these rows directly; do not assume JSON output for `analyze`.
