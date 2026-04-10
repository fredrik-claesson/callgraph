---
name: pr2-deep-pr-review
description: Deep PR review with eight explicit passes and parallel sub-agent finding validation.
---

# PR2 - Deep Pull Request Review

PR reference argument: `[pr-number-or-url]` (optional).

If argument is:
- number: `gh pr view <number>`
- URL: `gh pr view "<url>"`
- missing: `gh pr view` (current branch PR)

## Phase 1: Fetch PR Context
- `gh pr view <arg>`
- `gh pr diff <arg> --name-only`
- `gh pr diff <arg>`
- `gh pr checks <arg> 2>/dev/null || true`
- Capture: title, description, goal/problem, author, base/head, linked issues.
- Read all changed files in full.

## Phase 2: Independent Review Passes
Perform all passes independently and produce candidate findings for each.

### Review Pass 1 - Problem Resolution
Does the code change actually solve the problem/goal stated in the PR description?
- Map each stated requirement to the code changes that address it.
- Flag anything stated as fixed/implemented that appears missing or only partially addressed.
- Flag any stated non-goals that appear to have been inadvertently changed.

### Review Pass 2 - Coding Conventions & Rules
- Check naming conventions (variables, functions, classes, files) for consistency with surrounding codebase.
- Check formatting, import ordering, and file-organization patterns visible in unchanged surrounding code.
- Detect project-specific patterns (error handling, logging, dependency injection) being violated.
- Flag use of patterns that are inconsistent with similar code in the repository.

### Review Pass 3 - Obsolete / Deprecated Code
- Flag calls to APIs, libraries, or language features marked deprecated or scheduled for removal.
- Flag patterns the codebase is actively migrating away from (use TODO/FIXME/DEPRECATED hints).
- Flag outdated syntax that newer language/framework versions replace with better alternatives.

### Review Pass 4 - Test Coverage
- Determine whether changed behavior has corresponding test additions or updates.
- Identify code paths, branches, and edge cases that lack test coverage.
- If testing is structurally infeasible (for example infrastructure glue or UI layout), mark explicitly as "infeasible, not a finding".
- Flag tests that exist but only cover happy paths while code adds new error paths.

### Review Pass 5 - Race Conditions & Concurrency
- Look for shared mutable state accessed from multiple goroutines/threads/async contexts without synchronization.
- Look for TOCTOU (time-of-check-time-of-use) patterns.
- Look for improper async/await usage that could cause interleaving issues.
- Look for missing locks, channels, or atomic operations where needed.

### Review Pass 6 - Performance
- Look for N+1 query patterns or queries inside loops.
- Look for unnecessary recomputation inside hot paths or tight loops.
- Look for large allocations or copies that can be avoided.
- Look for missing caching where results are stable and repeatedly accessed.
- Look for O(n^2) or worse algorithms where a more efficient approach exists.

### Review Pass 7 - Database Index Coverage
- For every new or modified query, check whether WHERE/ORDER BY/JOIN columns are covered by existing indexes.
- Flag queries likely to cause full table scans on large tables.
- Flag missing composite indexes for multi-column filtering.
- Check whether new migrations add appropriate indexes alongside new query columns.

### Review Pass 8 - Future Flag Readiness & Dual-Mode Test Coverage
- Assess whether introducing a feature flag would be desirable for rollout safety, blast-radius control, or gradual adoption.
- If a feature flag is present or recommended, verify regression coverage with flag OFF.
- Verify new behavior coverage with flag ON.
- Flag missing test matrices for both modes (OFF regression + ON behavior).

Candidate finding schema:
- `id`
- `category`
- `title`
- `location`
- `evidence`
- `hypothesis`

## Phase 3: Consolidate Candidate Findings
Print before validation:

```text
## Candidate Findings (N total)

[id]  [category]  [title]
      Location: [file:line]
      Evidence: [quote or reference]
```

## Phase 4: Parallel Sub-Agent Validation
Launch one sub-agent per finding in parallel. Each sub-agent gets:
- full PR diff
- finding details (`id`, `category`, `title`, `location`, `evidence`, `hypothesis`)
- validation task and required output

Required sub-agent output:

```text
FINDING: [id]
VERDICT: valid | invalid | partial
IMPACT: critical | high | medium | low | informational
JUSTIFICATION: [2-4 sentences with specific evidence]
SUGGESTED FIX: [concrete suggestion, or "N/A" if invalid]
```

## Phase 5: Synthesise Final Report
Produce:
- PR metadata summary
- Problem Resolution assessment
- Validated Findings by severity
- Discarded Findings
- Summary and final recommendation:
  - `APPROVE`
  - `REQUEST CHANGES`
  - `NEEDS DISCUSSION`

For each confirmed finding include:
- ID, title, location, impact, justification, suggested fix.
