# /callgraph-search-method

Find C# methods by name using the CallGraph index (fast) via CLI.

## Search behavior
- Non-regex search is hybrid:
  - lexical token split over namespace/class/method/signature
  - top-K semantic rerank with local `bge-small-en-v1.5`
- `--regex` bypasses semantic rerank and runs regex-only search.
- Missing model files automatically degrade to lexical-only ranking.

## Execution policy
- Run commands in foreground only and always append `2>&1`.
- Use daemon mode first: `callgraph search-method ... 2>&1`.
- Retry with `--no-daemon` only on timeout/error/inconsistent output:
  `callgraph search-method ... --no-daemon 2>&1`.
- Identifier-known branch:
  - Step 1: if class is known but file is unknown, resolve file with `search-file --pattern "*<ClassName>.cs"` first.
  - Step 2: run one narrow identifier-first query with `--pattern` (class + method when available).
  - Step 3: if no clear match, loosen one dimension at a time (scope or wildcards), rerun once, then reassess.
- Exploratory branch (no concrete identifiers):
  - Step 1: run one scoped semantic `--keywords` query first.
  - Step 2: refine keywords/scope once if needed.
  - Step 3: switch to identifier/pattern only after keyword results reveal concrete names.
- Final fallback: `--regex` only as strict fallback.
- Never run parallel/background triangulation searches unless the user explicitly asks for parallel search.
- Avoid wildcard-heavy first queries unless explicitly requested.

## Prereqs
- CallGraph CLI is available (`callgraph` binary or `dotnet run --project CallGraph.csproj --`)

## Inputs
- `pattern` (preferred): identifier-based wildcard pattern like `*AdyenBalanceCommunicationComponent*GetBalanceAccountAsync*`
- `keywords` (fallback): semantic intent terms when identifiers are unknown
- `--regex` (optional): treat keywords/pattern as regex (default false)
- `--solutionPath` / `--solutionId` (optional): filter to specific solution
- If no keywords/pattern is provided and full inventory is requested, use `/callgraph-list-methods` instead.

## Scope rule
- If the containing file is known, include `--filePath <file.cs>` to keep results file-scoped.
- If only a folder is known, include `--folderPath <folder>` before searching project-wide.
- Use broad project-wide method searches only when explicitly requested.
- Prefer adding scope before broadening keyword sets.

## Action
Run CLI:
`callgraph search-method --pattern <pattern> [--keywords <keywords>] [--regex] [--solutionPath <path>] [--solutionId <id>] [--folderPath <folder>] [--filePath <file.cs>]`

## Output
- Show ranked list of matches (type + display + file + line)
- If 0 matches: suggest broader pattern or --regex
- If too many: suggest narrowing pattern or adding --solutionPath

## Query guidance
- Prefer identifier-based pattern search when class/method names are known.
- Prefer semantic keyword search first for exploratory questions without concrete names.
- Use `--regex` for exact structural matching only.
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
- `search-method` returns plain text, one match per line:
  `<filePath[:line]>\t<containingType>\t<methodName>\t<signature>`.
- Forbidden:
  - `python3 << 'EOF' ...`
  - temporary/custom parser scripts
