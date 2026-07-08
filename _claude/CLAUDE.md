# C# Code Intelligence

CallGraph indexes a C# solution into a local SQLite database and offers two ways to interrogate it:

- `callgraph query "<SQL>"` — read-only SQL over the indexed database (files, methods, types, one-hop
  callers/callees). See the `callgraph-sql` skill for the schema and worked examples.
- `callgraph analyze --filepath <file.cs> [...]` — multi-hop call-graph traversal (callers/callees,
  visibility- and depth-aware). See the `callgraph-analyze-callgraph` skill.

Prefer `callgraph query` for file/method/type lookups, and `callgraph analyze` for tracing call
relationships across more than one hop. If the index is missing or stale, run `callgraph --index <sln>`
or `callgraph --reindex` before either command.

Index scope note: CallGraph indexing/analysis excludes test projects and the source files in those test
projects.
