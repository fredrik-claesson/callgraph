---
description: Apply the CallGraph C# workflow playbooks (UnknownEntrypoints, KnownEntrypoints, KnownComponentImpact, LargeRefactorPlanning).
agent: build
---

Use the `callgraph-playbooks` skill and select the most suitable scenario for the current task.

Start with a one-sentence scenario declaration, then execute the workflow with CallGraph-first command selection.

Required checkpoints:
- `scope checkpoint`: `file | method(s) | why relevant | confidence`
- `expansion checkpoint`: `unknowns | next tools | expected value`
