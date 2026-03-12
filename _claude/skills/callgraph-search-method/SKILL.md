---
name: callgraph-search-method
description: Fast indexed search for C# methods via CallGraph CLI. Use when asked to find methods by name or pattern.
---

# C# Search Method (Indexed)

## Search behavior
- Non-regex search is hybrid:
  - lexical token split over namespace/class/method/signature
  - top-K semantic rerank with local `bge-small-en-v1.5`
- `--regex` bypasses semantic rerank and runs regex-only search.
- If model assets are missing, search falls back to lexical-only ranking.

## Execution policy
- Identifier-known branch:
  - Step 1: if class is known but file is unknown, run `search-file --pattern "*<ClassName>.cs"` with narrow scope.
  - Step 2: run one narrow identifier-based query with `--pattern` (class + method when available).
  - Step 3: if no clear match, loosen one dimension at a time (scope or wildcard pattern), rerun once, then reassess.
- Exploratory branch (no concrete identifiers, e.g. "where/how/which calculates X"):
  - Step 1: run one scoped semantic `--keywords` query first.
  - Step 2: refine keywords/scope once if needed.
  - Step 3: switch to identifier/pattern only after keyword results reveal concrete names.
- Final fallback: `--regex` only for strict pattern matching.
- Never run parallel/background triangulation searches unless the user explicitly asks for parallel search.

## Inputs
- pattern (preferred): identifier-based wildcard pattern like `*AdyenBalanceCommunicationComponent*GetBalanceAccountAsync*`
- keywords (fallback): semantic intent terms when identifiers are unknown
- --regex (optional): treat keywords/pattern as regex (default false)
- --solutionPath / --solutionId (optional): filter to specific solution
- Use `callgraph-list-methods` when no name/pattern filter is provided and the user wants all methods.

## Scope rule
- If the containing file is known, pass `--filePath <file.cs>` to keep results file-scoped.
- If only a folder is known, pass `--folderPath <folder>` before searching wider scopes.
- Prefer adding scope before broadening keyword sets.

## Action
Run CLI:
`callgraph search-method --pattern <pattern> [--keywords <keywords>] [--regex] [--solutionPath <path>] [--solutionId <id>] [--folderPath <folder>] [--filePath <file.cs>]`

## Output
- Show ranked list of matches (type + display + file + line)
- If 0 matches: suggest broader pattern or --regex
- If too many matches: suggest narrowing pattern or adding --solutionPath

## Query guidance
- Prefer identifier pattern search when class/method names are known.
- Prefer semantic keyword search first for exploratory questions without concrete names.
- Use `--regex` only for strict pattern matching.
- Example for this intent:
  - first: `--pattern "*AdyenBalanceCommunicationComponent*GetBalanceAccountAsync*"`
  - exploratory example: `--keywords "adyen interchange fee calculation"`

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
  - generating one-off parser scripts
