---
name: callgraph-get-method-source
description: Extract exact live C# method content from a known file path.
---

# C# Get Method Source

Use this after method discovery to fetch exact implementation text from current source files without grep/chunk scanning.
- When identifying candidate methods in a known file/folder, prefer scoped discovery flow: `list-methods` (scoped, live signatures) -> `search-method` (targeted index search) -> `get-method-source` (live body). Avoid bulk file reads until candidates are narrowed.

## Execution policy
- Run in foreground and append `2>&1`.
- Use daemon mode first:
  `callgraph get-method-source ... 2>&1`
- Retry only on timeout/error/inconsistent output:
  `callgraph get-method-source ... --no-daemon 2>&1`

## Required input
- `--filePath <absolute .cs file>`
- At least one selector: `--methodName`, `--signature`, or `--startLine`

## Optional selectors
- `--containingType <Namespace.Type>`
- `--signature "..."`
- `--startLine <n>`

## Modes
- `signature_only`
- `signature_plus_body` (default)
- `body_only`
- `body_without_comments`

## Action
Run CLI:
`callgraph get-method-source --filePath <file.cs> [--methodName <name>] [--containingType <type>] [--signature <signature>] [--startLine <n>] [--mode <mode>]`

## Output
Structured JSON with selected method metadata and content:
`filePath`, `methodName`, `containingType`, `signature`, `startLine`, `endLine`, `startByte`, `endByte`, `mode`, `content`.
