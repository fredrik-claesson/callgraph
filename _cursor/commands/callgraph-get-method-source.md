# /callgraph-get-method-source

Extract exact live C# method content from a known file path.

## When to use
- File path is already known and you need the current implementation text.
- You want token-efficient extraction without grep/chunk scanning.

## Execution policy
- Run commands in foreground only and always append `2>&1`.
- Use daemon mode first: `callgraph get-method-source ... 2>&1`.
- Retry with `--no-daemon` only on timeout/error/inconsistent output:
  `callgraph get-method-source ... --no-daemon 2>&1`.
- Do not chain multiple `callgraph get-method-source` calls using `&&`/`;` in one command. Run one request per command and synthesize after.

## Inputs
- Required: `--filePath <absolute .cs file>`
- Required selectors: at least one of `--methodName`, `--signature`, or `--startLine`
- Optional: `--containingType <Namespace.Type>`
- Optional mode: `--mode signature_only|signature_plus_body|body_only|body_without_comments`
- Prefer `--mode body_only` (or `body_without_comments`) for token-efficient reads; use `signature_plus_body` only when signature context is explicitly required.

## Action
Run CLI:
`callgraph get-method-source --filePath <file.cs> [--methodName <name>] [--containingType <type>] [--signature <signature>] [--startLine <n>] [--mode <mode>]`

## Output
Structured JSON:
- `filePath`, `methodName`, `containingType`, `signature`
- `startLine`, `endLine`, `startByte`, `endByte`
- `mode`, `content`
