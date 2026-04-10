---
description: Deep PR review with sub-agent finding validation.
argument-hint: "[pr-number-or-url]"
allowed-tools: Bash, Read, Glob, Grep, Task
agent: build
---

Run the `pr2-deep-pr-review` skill.

Execution protocol:
1. Fetch PR context with `gh pr view` / `gh pr diff` / `gh pr checks`.
2. Read every changed file fully.
3. Execute all eight explicit review passes independently (including future-flag readiness and dual-mode flag OFF/ON coverage).
4. Print candidate findings.
5. Launch one parallel sub-agent per candidate for validation.
6. Produce final PR Review Report with recommendation.

Recommendation must be one of:
- `APPROVE`
- `REQUEST CHANGES`
- `NEEDS DISCUSSION`
