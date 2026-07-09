# CallGraph plugin

A self-contained [Claude Code plugin](https://code.claude.com/docs/en/plugins.md) that bundles the
CallGraph CLI binary together with its two skills, so a coding agent can index and navigate C#
solutions in any repository without a separate install.

## What's inside

```
plugins/callgraph/
├── .claude-plugin/
│   └── plugin.json                 # plugin manifest
├── skills/
│   ├── callgraph-sql/              # read-only SQL over the index (one-hop who-calls-whom)
│   └── callgraph-analyze-callgraph/# multi-hop reachability / blast-radius traversal
├── bin/
│   ├── callgraph                   # POSIX launcher (dispatches to bin/<rid>/CallGraph)
│   ├── callgraph.cmd               # Windows launcher
│   └── <rid>/CallGraph[.exe]       # self-contained binaries (gitignored — built on demand)
└── scripts/
    ├── build-binaries.sh           # publish per-RID binaries into bin/<rid>/
    └── package-plugin.sh           # tar a self-contained marketplace archive into ../dist/
```

> Packaged archives are written to `plugins/dist/` (a sibling of this directory), **not** inside the
> plugin, so a local-path marketplace install never copies the archive into the installed plugin cache.

When the plugin is active, `bin/` is added to the Bash tool's `PATH`, so the skills invoke
`callgraph …` and the launcher runs the correct binary for the host OS/arch. The skills are byte-for-byte
copies of the repo's `_claude/skills/` — same content, just delivered as a plugin.

## Build the binaries

The binaries are large and rebuilt often, so they are **gitignored**. Build them before packaging:

```bash
# all runtimes (osx-arm64, osx-x64, linux-x64, win-x64)
plugins/callgraph/scripts/build-binaries.sh

# or a subset
plugins/callgraph/scripts/build-binaries.sh osx-arm64 linux-x64
```

## Redistribute

Because the binaries are not committed, hand off a packaged archive (binaries included):

```bash
plugins/callgraph/scripts/package-plugin.sh          # -> plugins/dist/callgraph-marketplace-<version>.tar.gz
```

The archive unpacks to a directory that IS a self-contained marketplace
(`.claude-plugin/marketplace.json` + `callgraph/`). The recipient unpacks it anywhere and points a
marketplace at that directory:

```
tar -xzf callgraph-marketplace-<version>.tar.gz -C ~/some/dir
/plugin marketplace add ~/some/dir/callgraph-marketplace
/plugin install callgraph@callgraph-marketplace
```

Or, straight from this repo's root marketplace manifest (`.claude-plugin/marketplace.json`):

```
/plugin marketplace add fredrik-claesson/callgraph
/plugin install callgraph@callgraph-marketplace
```

> **Note on git-based install:** a marketplace that clones this repo will **not** receive the binaries
> (they are gitignored). For git/GitHub distribution either (a) run `build-binaries.sh` and commit the
> `bin/<rid>/` binaries on a release branch/tag, or (b) distribute the `package-plugin.sh` archive / a
> local unpacked copy. The manifests, launchers, and skills are always tracked.

## First run in a target repo

```bash
callgraph --index /abs/path/to/solution.sln   # build the index once
callgraph query "SELECT COUNT(*) FROM Methods"
callgraph analyze --filepath /abs/File.cs --depth 1
```
