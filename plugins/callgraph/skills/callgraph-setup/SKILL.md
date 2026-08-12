---
name: callgraph-setup
description: First-time setup for the CallGraph plugin — downloads and installs the CallGraph binary for the host platform from GitHub Releases. Run once per machine ("setup callgraph" or "/callgraph:callgraph-setup").
---

# CallGraph Setup

You are the CallGraph setup skill. Your job is to ensure the `callgraph` binary is installed and working. Work through each step in order.

## Step 1 — Check if already installed

Run `callgraph --version`.

If it succeeds, print the version and stop:

> CallGraph is already installed (`<version>`). No action needed.

## Step 2 — Detect platform

Run `uname -s` and `uname -m`.

| uname -s | uname -m | RID | Asset |
|---|---|---|---|
| Darwin | arm64 | osx-arm64 | `callgraph-osx-arm64.tar.gz` |
| Darwin | x86_64 | osx-x64 | `callgraph-osx-x64.tar.gz` |
| Linux | x86_64 or amd64 | linux-x64 | `callgraph-linux-x64.tar.gz` |
| (uname not found) | — | win-x64 | `callgraph-win-x64.zip` |

If the platform is unsupported, stop and tell the user:

> CallGraph doesn't have a pre-built binary for your platform (`<os>/<arch>`). You can build from source: clone `https://github.com/fredrik-claesson/callgraph` and run `dotnet publish CallGraph.csproj -c Release -r <your-rid> --self-contained true -o ~/.callgraph/bin/<your-rid>`.

## Step 3 — Check prerequisites

**`gh` CLI required.** Run `gh --version`. If not found, tell the user:

> The `gh` CLI is required to download CallGraph from GitHub Releases.
> Install it:
> - macOS: `brew install gh`
> - Windows: `winget install --id GitHub.cli -e`
> - Linux: https://cli.github.com/
>
> Then run setup again.

Stop if `gh` is missing.

## Step 4 — Download the binary

Run:

```bash
INSTALL_DIR="$HOME/.callgraph/bin/$RID"
mkdir -p "$INSTALL_DIR"
gh release download --repo fredrik-claesson/callgraph --pattern "callgraph-$RID.tar.gz" --dir /tmp --clobber
tar -xzf "/tmp/callgraph-$RID.tar.gz" -C "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/CallGraph"
```

On Windows (zip asset):

```powershell
$installDir = "$env:APPDATA\callgraph\bin\win-x64"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
gh release download --repo fredrik-claesson/callgraph --pattern "callgraph-win-x64.zip" --dir $env:TEMP --clobber
Expand-Archive -Path "$env:TEMP\callgraph-win-x64.zip" -DestinationPath $installDir -Force
```

If the download fails because no release exists yet, tell the user:

> No release found in `fredrik-claesson/callgraph`. You may need to build from source or ask the plugin maintainer to publish a release.

## Step 5 — Verify

Run `callgraph --version` again (the plugin's launcher wrapper will now find the binary at `~/.callgraph/bin/$RID/CallGraph`).

If it succeeds, print:

> CallGraph installed successfully (`<version>`). The plugin is ready to use.

If it still fails, check that `~/.callgraph/bin/<rid>/CallGraph` is executable and that the plugin is active. Advise the user to restart Claude Code if the plugin wrapper was not yet in PATH.
