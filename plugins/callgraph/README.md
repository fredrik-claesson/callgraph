# CallGraph plugin

A [Claude Code plugin](https://code.claude.com/docs/en/plugins.md) that gives a coding agent
Roslyn-powered C# call-graph navigation via three skills and a CLI launcher. The binary is
**not bundled** — it is downloaded on first use by the `callgraph-setup` skill.

## What's inside

```
plugins/callgraph/
├── .claude-plugin/
│   └── plugin.json                     # plugin manifest
├── skills/
│   ├── callgraph-setup/                # first-time binary install (downloads from GitHub Releases)
│   ├── callgraph-sql/                  # read-only SQL over the index (one-hop who-calls-whom)
│   └── callgraph-analyze-callgraph/    # multi-hop reachability / blast-radius traversal
├── bin/
│   ├── callgraph                       # POSIX launcher (checks ~/.callgraph/bin/<rid>/ first)
│   └── callgraph.cmd                   # Windows launcher (checks %APPDATA%\callgraph\bin\win-x64\)
└── scripts/
    ├── build-binaries.sh               # publish per-RID binaries into bin/<rid>/  (maintainer use)
    ├── release.sh                      # build + upload binaries as a GitHub Release (maintainer use)
    └── package-plugin.sh               # tar a local marketplace archive into ../dist/
```

When the plugin is active, `bin/` is added to the Bash tool's `PATH`, so the skills invoke
`callgraph …` and the launcher finds the binary installed by `callgraph-setup`.

## First-time setup (users)

After installing the plugin, run the setup skill once:

```
/callgraph:callgraph-setup
```

This downloads the correct binary for your platform from the latest GitHub Release and installs it
to `~/.callgraph/bin/<rid>/CallGraph` (Mac/Linux) or `%APPDATA%\callgraph\bin\win-x64\CallGraph.exe`
(Windows). Requires the `gh` CLI to be installed and authenticated.

## Using the index

```bash
callgraph --index /abs/path/to/solution.sln   # build the index once
callgraph query "SELECT COUNT(*) FROM Methods"
callgraph analyze --filepath /abs/File.cs --depth 1
```

## Publishing a new release (maintainers)

Binaries are distributed as GitHub Release assets — not committed to git. To cut a new release:

```bash
plugins/callgraph/scripts/release.sh v1.0.1          # build, package, and publish
plugins/callgraph/scripts/release.sh v1.0.1 --draft  # create a draft first
```

This runs `build-binaries.sh` for all RIDs and uploads `callgraph-<rid>.tar.gz` / `callgraph-win-x64.zip`
as assets to the `fredrik-claesson/callgraph` GitHub repo. Requires `gh` CLI and `dotnet` SDK.

## Local / CI packaging (optional)

If you need a fully self-contained archive (binaries included, no download required):

```bash
plugins/callgraph/scripts/build-binaries.sh          # builds into bin/<rid>/
plugins/callgraph/scripts/package-plugin.sh          # -> plugins/dist/callgraph-marketplace-<version>.tar.gz
```

The archive unpacks to a directory that IS a Claude Code marketplace. The recipient can install
without touching GitHub:

```
tar -xzf callgraph-marketplace-<version>.tar.gz -C ~/some/dir
/plugin marketplace add ~/some/dir/callgraph-marketplace
/plugin install callgraph@callgraph-marketplace
```
