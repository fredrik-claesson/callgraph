---
name: callgraph-update
description: Update the CallGraph binary to the latest GitHub Release, replacing whatever is currently installed. Run when a new release has been published ("update callgraph").
---

# CallGraph Update

You are the CallGraph update skill. Download and install the latest CallGraph binary for the host platform, unconditionally replacing any existing installation.

## Step 1 — Detect platform

Run `uname -s` and `uname -m`.

| uname -s | uname -m | RID | Asset |
|---|---|---|---|
| Darwin | arm64 | osx-arm64 | `callgraph-osx-arm64.tar.gz` |
| Darwin | x86_64 | osx-x64 | `callgraph-osx-x64.tar.gz` |
| Linux | x86_64 or amd64 | linux-x64 | `callgraph-linux-x64.tar.gz` |
| (uname not found) | — | win-x64 | `callgraph-win-x64.zip` |

If the platform is unsupported, stop and tell the user which RIDs are available.

## Step 2 — Check prerequisites

Run `gh --version`. If not found, tell the user to install it (`brew install gh` / `winget install --id GitHub.cli -e`) and stop.

## Step 3 — Download and install

**Mac/Linux:**

```bash
INSTALL_DIR="$HOME/.callgraph/bin/$RID"
mkdir -p "$INSTALL_DIR"
gh release download --repo fredrik-claesson/callgraph --pattern "callgraph-$RID.tar.gz" --dir /tmp --clobber
tar -xzf "/tmp/callgraph-$RID.tar.gz" -C "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/CallGraph"
```

**Windows:**

```powershell
$installDir = "$env:APPDATA\callgraph\bin\win-x64"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
gh release download --repo fredrik-claesson/callgraph --pattern "callgraph-win-x64.zip" --dir $env:TEMP --clobber
Expand-Archive -Path "$env:TEMP\callgraph-win-x64.zip" -DestinationPath $installDir -Force
```

## Step 4 — Verify

Run `callgraph --version` and print the result:

> CallGraph updated to `<version>`.
