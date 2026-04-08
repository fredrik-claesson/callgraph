# OpenCode integration assets

This folder contains global assets for OpenCode.

## Installed by `callgraph install`

If `~/.config/opencode` exists, `callgraph install` copies:
- `skills/*` to `~/.config/opencode/skills/*`
- `agents/*.md` to `~/.config/opencode/agents/*.md`
- `commands/*.md` to `~/.config/opencode/commands/*.md`
- `plugins/*.js` to `~/.config/opencode/plugins/*.js`

OpenCode loads local plugins from `~/.config/opencode/plugins` automatically.

## Plugin policy mode

- `OPENCODE_CALLGRAPH_POLICY_MODE=warn|deny` (default `warn`)
- `warn`: allow command execution and emit hook hints to logs
- `deny`: throw hook errors and block policy violations
- Fallback threshold remains configurable with `OPENCODE_CALLGRAPH_FALLBACK_AFTER_FAILURES` (default `2`)
- Plugin hints include explicit guidance for common mistakes (for example `callgraph analyze --methodName` should be `--method`, and `get-method-source` should usually use `--mode body_only`).

## Git commit self-review gate

- The plugin intercepts `git commit` and blocks it unless a decision marker is present.
- On first commit attempt (no marker), the agent is instructed to ask user whether to run self-review.
- If user wants self-review, workflow must follow PR2 deep review:
  - load context from PR (`gh pr ...`) when available, otherwise local pre-commit diffs
  - run all seven review passes (problem resolution, conventions, deprecated code, tests, concurrency, performance, DB index coverage)
  - produce candidate findings
  - launch one parallel sub-agent per finding for validation
  - synthesize final report and recommendation
  - only if recommendation is `APPROVE`, commit with:
  - `CALLGRAPH_GIT_SELF_REVIEW=approved git commit ...`
- If user declines self-review, commit with:
  - `CALLGRAPH_GIT_SELF_REVIEW=skip git commit ...`
- PowerShell marker format is also supported:
  - `$env:CALLGRAPH_GIT_SELF_REVIEW='approved'; git commit ...` (or `'skip'`)

## Agent

After install, invoke the CallGraph-focused subagent with:

```text
@callgraph-csharp trace inbound callers of ProcessPaymentAsync
```

## Playbook command

After install, run:

```text
/callgraph-playbook
```

This command applies the scenario playbook workflow from `callgraph-playbooks`.
