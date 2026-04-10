# /pr2-deep-pr-review

Deep PR review with sub-agent finding validation.

Argument hint: `[pr-number-or-url]` (optional).

Allowed tools:
- Bash
- Read
- Glob
- Grep
- Task

## Phase 1: Fetch PR Context
Use `gh pr view`:
- number argument: `gh pr view <number>`
- URL argument: `gh pr view "<url>"`
- no argument: `gh pr view`

Collect:
- PR title, description, stated goal/problem
- author, base branch, head branch
- changed files: `gh pr diff <arg> --name-only`
- full diff: `gh pr diff <arg>`
- checks: `gh pr checks <arg> 2>/dev/null || true`
- linked issues in body

Read every changed file in full before reviewing.

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

Each candidate finding must include:
- `id`
- `category`
- `title`
- `location`
- `evidence`
- `hypothesis`

## Phase 3: Consolidate Candidate Findings
Print:

```text
## Candidate Findings (N total)

[id]  [category]  [title]
      Location: [file:line]
      Evidence: [quote or reference]
```

Do not pre-filter before validation.

## Phase 4: Parallel Sub-Agent Validation
For every candidate finding, launch one independent sub-agent in parallel.

Each sub-agent gets:
1. full PR diff
2. finding to validate (`id`, `category`, `title`, `location`, `evidence`, `hypothesis`)
3. validation task:
   - verdict: `valid | invalid | partial`
   - impact: `critical | high | medium | low | informational`
   - justification: 2-4 sentences with specific evidence
   - suggested fix if valid

Required sub-agent output:

```text
FINDING: [id]
VERDICT: valid | invalid | partial
IMPACT: critical | high | medium | low | informational
JUSTIFICATION: [2-4 sentences with specific evidence]
SUGGESTED FIX: [concrete suggestion, or "N/A" if invalid]
```

Use `subagent_type: general-purpose`.

## Phase 5: Synthesise Final Report
Return:

```markdown
# PR Review Report

**PR:** [title] (#[number])
**Author:** [author]
**Base -> Head:** [base] -> [head]

---

## Problem Resolution
[1-2 sentences]

---

## Validated Findings

### Critical (must fix before merge)
[list or "None"]

### High (should fix)
[list or "None"]

### Medium (worth addressing)
[list or "None"]

### Low / Informational
[list or "None"]

---

## Discarded Findings (invalid after validation)
[candidate IDs + short rationale]

---

## Summary & Recommendation
[3-5 sentences]
**Recommendation:** APPROVE | REQUEST CHANGES | NEEDS DISCUSSION
```

For each confirmed finding include ID, title, location, impact, justification, and suggested fix.
