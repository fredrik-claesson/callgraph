# /callgraph-sequence-diagram

Generate a Mermaid `sequenceDiagram` from CallGraph analyzer output.

## Command execution policy
- Run commands in foreground only and always append `2>&1`.
- Use daemon mode first for latency, then retry with `--no-daemon` only on timeout/error/inconsistent output.

## Inputs
- `filepath` (required)
- `method` (optional, case-sensitive)
- `depth` (optional, default 1)
- `direction` (optional): `inbound|outbound|bi-directional` (default `bi-directional`)
- `visibility` (optional): `external|internal` (default `external`)
- `solutionPath` / `solutionId` (optional, only when ambiguity exists)

## Scope rule
- Use the provided `filepath` directly and keep analysis scoped to that file.
- Do not broaden to project-wide discovery unless resolving explicit ambiguity.

## Workflow
1. Run `callgraph analyze` for the target file/method.
2. Build Mermaid `sequenceDiagram` from analyzer output only.
3. Save to `docs/diagrams/<FileBase>.<Method?>.sequence.md`.

## Error handling
- `IndexNotReady`: tell user to run with `--index`/`--reindex`.
- `AmbiguousSolution`: retry with `--solutionPath`/`--solutionId`.
- `TargetsNotFound`: report clearly and suggest method/file refinement.

## Diagram rules
- Use analyzer output as the single source of truth.
- Do not invent nodes or edges.
- Participant label format:
  `<ContainingType>.<MethodName> (<FileName>:<Line>)`

## Shaping policy
- Max participants: 14
- Per target: up to 3 inbound + 3 outbound paths (closest first)
- Participant order: inbound farthest->closest, targets, outbound closest->farthest
