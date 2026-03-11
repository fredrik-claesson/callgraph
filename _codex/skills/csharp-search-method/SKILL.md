---
name: csharp-search-method
description: Fast indexed search for C# methods via CallGraph CLI. Much faster than repo scanning.
metadata:
  short-description: "Indexed method search (CLI)"
---

## Why this skill
Queries a prebuilt analyzer index - much faster than scanning with `rg`/`grep` on large solutions. Returns structured method metadata.

## Tool
Use CLI command: `callgraph search-method`
- MUST run `callgraph search-method` in the foreground (blocking call). Do not start background terminals for this command.
- If a background terminal is started accidentally, stop it and rerun in foreground immediately.

## Search behavior
- Non-regex search is hybrid:
  - lexical token split over namespace/class/method/signature
  - top-K semantic rerank with local `bge-small-en-v1.5` embeddings
- `--regex` bypasses semantic rerank and uses regex-only search in SQLite.
- If model files are missing, command falls back to lexical-only ranking automatically.

## Query mode selection
Choose mode by prompt type:
1. Identifier-known prompt (class/method/namespace names provided): use identifier-first path (`search-file --pattern` then `search-method --pattern`).
2. Exploratory prompt (for example "where/how/which calculates X", no concrete identifiers): run semantic `--keywords` first.
3. Use `--regex` only when wildcard pattern cannot express required matching.

## Execution policy
- Identifier-first branch:
  - Step 1: if class is known but file is unknown, run `search-file --pattern "*<ClassName>.cs"` with narrow scope.
  - Step 2: run one narrow `search-method --pattern` query using known identifiers (class + method when available).
  - Step 3: if no clear match, loosen one dimension at a time (scope or wildcards), rerun once, then reassess.
- Exploratory branch:
  - Step 1: run one scoped `search-method --keywords` query first.
  - Step 2: refine keywords/scope once if needed.
  - Step 3: switch to identifier/pattern only after keywords return concrete candidates to drill into.
- Final fallback: use `--regex` only as strict final fallback.
- Never run parallel/background triangulation searches unless the user explicitly asks for parallel search.
- Do not start with wildcard-heavy patterns unless explicitly requested.

## Hard rule (Codex CLI)
- For this skill, execute exactly one foreground command at a time and wait for completion before any next step.
- Required command shape:
  `callgraph search-method ...`

## CLI Examples
```bash
callgraph search-method --pattern "*AdyenBalanceCommunicationComponent*GetBalanceAccountAsync*" --solutionId "solution-id"
```

```bash
callgraph search-method --keywords "login authentication" --solutionId "solution-id"
```

## Parameters
- `pattern` (preferred): identifier-oriented wildcard pattern (`*` and `?`, case-insensitive)
- `keywords` (fallback): semantic/general intent terms
- `regex` (optional): use SQLite REGEXP instead of wildcards
- `solutionPath` / `solutionId` (optional): filter to specific solution
- If no keywords/pattern is provided and a full inventory is requested, use `callgraph list-methods` instead.

## Scope rule
- If the containing file is known, pass `--filePath <file.cs>` to keep results file-scoped.
- If only a folder is known, pass `--folderPath <folder>` before searching project-wide.
- Prefer adding scope before broadening keyword sets.

## Output
- Show ranked list of matches (type + display + file + line)
- If 0 matches: suggest broader pattern or --regex
- If too many: suggest narrowing pattern or adding --solutionPath

## Query guidance
- Identifier-known query (preferred):
  - first: `--pattern "*AdyenBalanceCommunicationComponent*GetBalanceAccountAsync*"`
  - then (if needed): widen wildcards while preserving scope
- Exploratory semantic query (preferred for open questions):
  - first: `--keywords "adyen interchange fee calculation"`
  - then (if needed): refine terms with tighter scope

## Guardrails: prompt to first command
- Prompt: `use callgraph skill to list all callers to method GetBalanceAccountAsync in AdyenBalanceCommunicationComponent with call depth 2`
  - Expected first command: `callgraph search-file --pattern "*AdyenBalanceCommunicationComponent.cs"` (if file unknown)
  - Or if file already known: `callgraph search-method --pattern "*AdyenBalanceCommunicationComponent*GetBalanceAccountAsync*" --filePath <file.cs>`
- Prompt: `In this codebase, where are interchange fees calculated for Adyen payments?`
  - Expected first command: `callgraph search-method --keywords "adyen interchange fee calculation"`

## Output format note
- `search-method` now returns streamlined JSON records directly.
- Forbidden:
  - `python3 << 'EOF' ...`
  - custom one-off JSON parser scripts

## Known issues
- In some Codex CLI sessions, CallGraph commands may still hang despite foreground execution.
- If that happens, rerun Codex with `--yolo`.
