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

## Install shim and skills

Run installer from publish output:

```bash
./CallGraph install
```

Windows:

```powershell
.\CallGraph.exe install
```

Installer behavior:
- Copies bundled `_claude`, `_codex`, `_cursor` into `~/.claude`, `~/.codex`, `~/.cursor`.
- Installs `callgraph` shim:
  - macOS/Linux: first writable directory already on `PATH` (fallback: `~/.local/bin/callgraph`)
  - Windows: `%LocalAppData%\Programs\callgraph\callgraph.exe`
- Updates Windows user `PATH` automatically (new terminals).
- On macOS/Linux, prints a `PATH` export line if `~/.local/bin` is not currently on `PATH`.

Verify:

```bash
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

## Optional install flags

- `--skip-skills`: only install command shim
- `--skip-shim`: only deploy `_claude`/`_codex`/`_cursor`
- `--skip-path`: Windows only, do not update user PATH
- `--home <path>`: alternate home directory
- `--binDir <path>`: alternate shim install directory
