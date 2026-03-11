# QUICKSTART

Get CallGraph running end-to-end with CLI-only workflow.

## Requirements

- .NET SDK (repo targets `net10.0`)

## Build and publish

```bash
dotnet publish ./CallGraph.csproj -c Release -r osx-arm64 -o ./publish
```

This produces a single executable `CallGraph` (or `CallGraph.exe` on Windows) in `./publish/`.
For Windows, use `-r win-x64` (or your target RID).

## Make `callgraph` available system-wide

### macOS / Linux

Create a symlink from a directory on your `PATH` to the published executable:

```bash
# Using /usr/local/bin (requires sudo)
sudo ln -s "$(pwd)/publish/CallGraph" /usr/local/bin/callgraph

# Or, without sudo, using ~/.local/bin (ensure it is on your PATH)
mkdir -p ~/.local/bin
ln -s "$(pwd)/publish/CallGraph" ~/.local/bin/callgraph
```

To add `~/.local/bin` to your `PATH` if it isn't already:

```bash
# bash (~/.bashrc) or zsh (~/.zshrc)
export PATH="$HOME/.local/bin:$PATH"
```

Verify:

```bash
callgraph --help
```

### Windows

**Option A — Symlink (requires Developer Mode or an elevated prompt):**

```powershell
# In an elevated PowerShell prompt (or with Developer Mode enabled)
$target = (Resolve-Path .\publish\CallGraph.exe).Path
New-Item -ItemType SymbolicLink -Path "$env:LOCALAPPDATA\Programs\callgraph\callgraph.exe" -Target $target
# Then add the folder to your user PATH:
[Environment]::SetEnvironmentVariable(
    "PATH",
    "$env:PATH;$env:LOCALAPPDATA\Programs\callgraph",
    "User"
)
```

**Option B — Copy and add to PATH:**

```powershell
# Copy the publish output to a permanent location
$dest = "$env:LOCALAPPDATA\Programs\callgraph"
New-Item -ItemType Directory -Force -Path $dest
Copy-Item -Path .\publish\* -Destination $dest -Recurse

# Add to user PATH (takes effect in new terminals)
[Environment]::SetEnvironmentVariable(
    "PATH",
    "$env:PATH;$dest",
    "User"
)
```

Verify (in a new terminal):

```powershell
callgraph --help
```

## Analysis commands

```bash
# List indexed solutions
callgraph list-solutions   # auto-starts daemon on first call

# Search file/method
callgraph search-file --pattern "*Controller.cs"
callgraph search-method --keywords "login authentication"
callgraph list-methods --solutionPath "/abs/path/to/solution.sln"   # defaults to --visibility external

# Analyze call graph
callgraph analyze --filepath "/abs/path/to/File.cs" --depth 1 --direction bi-directional --visibility external

# Diagnostics
callgraph list-unused --projectPath "/abs/path/to/MyProject.csproj" --filePath "/abs/path/to/File.cs"
callgraph list-warnings --projectPath "/abs/path/to/MyProject.csproj" --filePath "/abs/path/to/File.cs"
```

## Optional daemon control

```bash
# Start daemon explicitly (optional)
callgraph serve

# Check if daemon is running
callgraph status

# Stop daemon
callgraph stop
```

By default, `serve` watches all currently indexed solutions. Use `callgraph serve --no-watch-indexed` if you want daemon caching without watcher overhead.
By default, `serve` exits after 10 hours of inactivity. Override with `callgraph serve --idleMinutes <n>`.

## Install sample commands/skills

### Claude Code

```bash
mkdir -p ./.claude/commands ./.claude/agents ./.claude/skills
cp -R ./_claude/commands/* ./.claude/commands/
cp -R ./_claude/agents/* ./.claude/agents/
cp -R ./_claude/skills/* ./.claude/skills/
```

### Cursor

```bash
mkdir -p ./.cursor/commands
cp -R ./_cursor/commands/* ./.cursor/commands/
```

### Other tooling

- `_claude/skills/` - Claude Code skill format
- `_codex/skills/` - Codex skill format (includes TOML interface definitions)
