---
name: callgraph-get-method-source
description: Extract exact live C# method content from a known file path using syntax-tree spans.
metadata:
  short-description: "Live method source extraction (CLI)"
---

## Why this skill
Returns exact method content from the current file on disk without grep/chunk scanning. Use this after discovery (`search-method`/`list-methods`) when you need implementation text.
- When identifying candidate methods in a known file/folder, prefer scoped discovery flow: `list-methods` (scoped, live signatures) -> `search-method` (targeted index search) -> `get-method-source` (live body). Avoid bulk file reads until candidates are narrowed.

## Tool
Use CLI command: `callgraph get-method-source`
- Run in foreground and always append `2>&1`.
- Use daemon mode first, and retry with `--no-daemon` only on timeout/error/inconsistent output.

## Required input
- `--filePath <absolute .cs file>`
- At least one selector: `--methodName`, `--signature`, or `--startLine`

## Optional selectors
- `--containingType <Namespace.Type>` to disambiguate overloads/partials
- `--signature "..."` for strict matching
- `--startLine <n>` for deterministic selection

## Modes
- `signature_only`
- `signature_plus_body` (default)
- `body_only`
- `body_without_comments`

## CLI Example
```bash
callgraph get-method-source --filePath "/abs/path/Foo.cs" --methodName "GetBalanceAccountAsync" --containingType "Demo.AdyenBalanceCommunicationComponent" --mode body_only 2>&1
```

## Output
- Structured JSON with:
  - `filePath`, `methodName`, `containingType`, `signature`
  - `startLine`, `endLine`
  - `startByte`, `endByte` (UTF-8 offsets)
  - `mode`, `content`
