---
name: callgraph-sequence-diagram
description: Generate a Mermaid sequence diagram by calling `callgraph analyze` and shaping the returned graph.
context: fork
agent: callgraph-haiku
---

# C# Sequence Diagram (Mermaid)

## Parameters
- filepath (required)
- method (optional, case-sensitive)
- depth (default 1)
- direction: inbound|outbound|bi-directional (default bi-directional)
- visibility: external|internal (default external)
- solutionPath/solutionId (only when filepath matches multiple solutions)

## Scope rule
- Use the provided `filepath` directly and keep analysis scoped to that file.
- Do not broaden to project-wide discovery unless resolving an explicit ambiguity.

## Error handling
- **IndexNotReady**: Instruct user to run CLI with `--index`/`--reindex`
- **AmbiguousSolution**: Run `callgraph search-file` to find candidates, retry with solutionPath
- **TargetsNotFound**: Report clearly, suggest providing method name

## Workflow
1. Run `callgraph analyze` to get the graph
2. Generate Mermaid sequenceDiagram using shaping policy below
3. Write to `docs/diagrams/<FileBase>.<Method?>.sequence.md`

## Response format (raw JSON, reduced details)
- `methodCount`: number of methods returned
- `callCount`: number of call edges returned
- `methods`: array of:
  - `methodId` (short ID, e.g. `m1`, `m2`)
  - `methodName`
  - `containingType`
  - `filePath`
  - `startLine`
- `calls`: array of:
  - `callerMethodId`
  - `calleeMethodId`
  - `direction` (`inbound`/`outbound`)

Use `methods` + `calls` directly. Use this raw response directly.

## Hard rules
- Use ONLY analyzer output for the diagram. Do NOT add edges/nodes from other sources.
- Participant labels: `<ContainingType>.<MethodName> (<FileName>:<Line>)`

## Shaping policy
- Max participants: 14
- Per target: up to 3 inbound + 3 outbound paths (closest first)

**Scoring:**
- +100 if target
- +30 if reachable inbound, +30 if reachable outbound
- -5 * min(distIn, distOut)
- +2 * degree

**Participant selection:**
1. Include targets
2. Best inbound nodes up to min(8, 3*depth)
3. Best outbound nodes up to min(8, 3*depth)
4. Cap at 14

**Participant order:** inbound farthest→closest, targets, outbound closest→farthest
