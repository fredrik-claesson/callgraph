# Copilot CLI integration assets

This folder contains user-level assets for GitHub Copilot CLI.

## Installed by `callgraph install`

If `~/.copilot` exists, `callgraph install` copies:
- `skills/*` to `~/.copilot/skills/*`
- `agents/*.agent.md` to `~/.copilot/agents/*.agent.md`
- `hooks/callgraph-pretooluse.sh` to `~/.copilot/hooks/callgraph-pretooluse.sh`
- `hooks/callgraph-pretooluse.hooks.json` to `~/.copilot/hooks/callgraph-pretooluse.hooks.json`

## Enable hooks (Copilot CLI)

Copilot CLI reads hook configuration from `.github/hooks/*.json` in your current working directory.

To enable the provided policy in a repository:

```bash
mkdir -p .github/hooks
cp ~/.copilot/hooks/callgraph-pretooluse.hooks.json .github/hooks/callgraph-pretooluse.hooks.json
chmod +x ~/.copilot/hooks/callgraph-pretooluse.sh
```

## Hook policy mode

- `COPILOT_CALLGRAPH_POLICY_MODE=warn|deny` (default `warn`)
- `warn`: allow command execution and return a policy hint
- `deny`: hard-block policy violations
- Fallback threshold remains configurable with `COPILOT_CALLGRAPH_FALLBACK_AFTER_FAILURES` (default `2`)

## Use the custom agent

```bash
copilot --agent callgraph-csharp --prompt "Trace callers of ProcessPaymentAsync in PaymentService"
```

## Use skills

Copilot auto-selects skills from `~/.copilot/skills` when relevant. You can also prompt explicitly with a slash-prefixed skill name, for example:

```text
Use the /callgraph-search-method skill to find where interchange fees are calculated.
```

For scenario-driven investigation and planning playbooks, use:

```text
Use the /callgraph-playbooks skill and apply the KnownComponentImpact scenario.
```
