# C# Code Intelligence
NEVER use `Grep`, `Glob`, or shell commands (`rg`, `find`, `grep`) for C# code searches.
ALWAYS use CallGraph skills instead:
- Find methods by name/pattern → `callgraph-search-method`
- Find files by name → `callgraph-search-file`
- List methods in a class/file → `callgraph-list-methods`
- Trace call dependencies → `callgraph-analyze-callgraph`
- Semantic/exploratory searches → `callgraph-search-method`
- Planning and gathering context → `callgraph-analyze-callgraph`

When spawning sub-agents for C# exploration, always include this instruction
explicitly in the prompt: "Use CallGraph skills (callgraph-search-method,
callgraph-list-methods, callgraph-analyze-callgraph) instead of grep/rg/find."