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
