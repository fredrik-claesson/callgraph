# Slim CallGraph to index / reindex / query / analyze — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce CallGraph to indexing (`--index`/`--reindex`/`--clear`), a read-only SQL `query` command, and recursive `analyze`, deleting the daemon, watcher, semantic search, diagnostics, extraction, install/rewrite, and the multi-agent bundled skill packs.

**Architecture:** Add the new `query` capability first (additive, build stays green), then rewire the CLI entrypoint to route only the surviving commands, then delete the now-unreferenced subsystems one at a time, each task ending with a green build + tests. Finally trim the project file, skills, scripts, and docs.

**Tech Stack:** .NET 10, Roslyn (Microsoft.CodeAnalysis), Microsoft.Data.Sqlite, Microsoft.Extensions.Hosting/DI.

## Global Constraints

- Target only `CallGraph.csproj`; build with `dotnet build CallGraph.csproj -v q`.
- Test with `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj --filter "<Name>" -v q`.
- No Fluent Assertions in new/modified tests; prefer idiomatic C# over FuncSharp.
- The DB path default is `<LocalApplicationData>/CallGraph/index.db`; the configured override is `IndexStoreOptions.DatabasePath` (config section `IndexStore`).
- Final CLI surface: `--index <sln>`, `--reindex [sln]`, `--clear`, `query "<SQL>"`, `analyze --filepath …`. Nothing else.
- `query` output: tab-separated, first line = column header, one row per record; read-only connection (`SqliteOpenMode.ReadOnly`); non-zero exit + stderr message on SQL error.
- Each removal task also deletes the corresponding test files so `dotnet test` stays green.

---

### Task 1: Shared index-DB path locator

Extract the DB-path resolution (currently private in `SqliteIndexStore` ctor, lines 24-30, and duplicated in `DispatchMapBuilder`) into a reusable static helper so the new `query` command resolves the identical path.

**Files:**
- Create: `Core/Indexing/IndexDatabaseLocator.cs`
- Modify: `Core/Indexing/SqliteIndexStore.cs:24-30` (use the helper)
- Test: `tests/CallGraph.Tests/Indexing/IndexDatabaseLocatorTests.cs`

**Interfaces:**
- Produces: `static string CallGraph.Core.Indexing.IndexDatabaseLocator.Resolve(string? configuredPath)` — returns `configuredPath` when non-blank, else `<LocalApplicationData>/CallGraph/index.db`.

- [ ] **Step 1: Write the failing test**

```csharp
using CallGraph.Core.Indexing;
using Xunit;

namespace CallGraph.Tests.Indexing;

public sealed class IndexDatabaseLocatorTests
{
    [Fact]
    public void Resolve_ReturnsConfiguredPath_WhenProvided()
    {
        var result = IndexDatabaseLocator.Resolve("/tmp/custom/index.db");
        Assert.Equal("/tmp/custom/index.db", result);
    }

    [Fact]
    public void Resolve_FallsBackToLocalApplicationData_WhenBlank()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CallGraph",
            "index.db");
        Assert.Equal(expected, IndexDatabaseLocator.Resolve(null));
        Assert.Equal(expected, IndexDatabaseLocator.Resolve("   "));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj --filter "IndexDatabaseLocatorTests" -v q`
Expected: FAIL — `IndexDatabaseLocator` does not exist.

- [ ] **Step 3: Create the helper**

```csharp
namespace CallGraph.Core.Indexing;

public static class IndexDatabaseLocator
{
    public static string Resolve(string? configuredPath)
        => string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CallGraph",
                "index.db")
            : configuredPath;
}
```

- [ ] **Step 4: Use it in SqliteIndexStore ctor**

Replace the inline fallback at `Core/Indexing/SqliteIndexStore.cs:26-30` so `_dbPath = IndexDatabaseLocator.Resolve(configuredPath);`.

- [ ] **Step 5: Run tests + build**

Run: `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj --filter "IndexDatabaseLocatorTests" -v q` → PASS
Run: `dotnet build CallGraph.csproj -v q` → succeeds.

- [ ] **Step 6: Commit**

```bash
git add Core/Indexing/IndexDatabaseLocator.cs Core/Indexing/SqliteIndexStore.cs tests/CallGraph.Tests/Indexing/IndexDatabaseLocatorTests.cs
git commit -m "Add reusable index-DB path locator"
```

---

### Task 2: Read-only `query` command executor

New class that opens the index DB read-only and runs arbitrary SQL, printing tab-separated rows with a header.

**Files:**
- Create: `Cli/QueryCommandExecutor.cs`
- Test: `tests/CallGraph.Tests/Cli/QueryCommandExecutorTests.cs`

**Interfaces:**
- Consumes: `IndexDatabaseLocator.Resolve` (Task 1).
- Produces: `Task<ToolExecutionResult> CallGraph.Cli.QueryCommandExecutor.ExecuteAsync(string sql, string? configuredDbPath, CancellationToken ct)`. `ToolExecutionResult` already exists in `Cli/ToolCommandExecutor.cs:994` as `internal sealed record ToolExecutionResult(int ExitCode, string? Stdout, string? Stderr)` with static factories `FromText` (sets `Stdout`, exit 0) and `FromError` (sets `Stderr`, non-zero exit). Output text (in `Stdout`): header line of tab-joined column names, then one tab-joined row per record; `NULL` rendered as empty string.

- [ ] **Step 1: Write the failing test**

```csharp
using CallGraph.Cli;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CallGraph.Tests.Cli;

public sealed class QueryCommandExecutorTests
{
    private static string SeedDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cg-query-{Guid.NewGuid():N}.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE Methods(Display TEXT, StartLine INTEGER); " +
                          "INSERT INTO Methods VALUES('FooService.Bar(int)', 42);";
        cmd.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsTabSeparatedRowsWithHeader()
    {
        var path = SeedDb();
        var result = await QueryCommandExecutor.ExecuteAsync(
            "SELECT Display, StartLine FROM Methods", path, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Display\tStartLine\nFooService.Bar(int)\t42", result.Stdout!.TrimEnd('\n'));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_OnWriteAttempt()
    {
        var path = SeedDb();
        var result = await QueryCommandExecutor.ExecuteAsync(
            "DELETE FROM Methods", path, CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj --filter "QueryCommandExecutorTests" -v q`
Expected: FAIL — `QueryCommandExecutor` does not exist.

- [ ] **Step 3: Implement the executor**

```csharp
using System.Text;
using CallGraph.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace CallGraph.Cli;

internal static class QueryCommandExecutor
{
    public static async Task<ToolExecutionResult> ExecuteAsync(
        string sql,
        string? configuredDbPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return ToolExecutionResult.FromError("query requires a SQL statement.");

        var dbPath = IndexDatabaseLocator.Resolve(configuredDbPath);
        if (!File.Exists(dbPath))
            return ToolExecutionResult.FromError($"Index database not found at {dbPath}. Run --index first.");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        try
        {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var sb = new StringBuilder();
            if (reader.FieldCount > 0)
            {
                sb.Append(reader.GetName(0));
                for (var i = 1; i < reader.FieldCount; i++)
                    sb.Append('\t').Append(reader.GetName(i));
                sb.Append('\n');
            }

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (i > 0) sb.Append('\t');
                    if (!reader.IsDBNull(i)) sb.Append(reader.GetValue(i));
                }
                sb.Append('\n');
            }

            return ToolExecutionResult.FromText(sb.ToString().TrimEnd('\n'));
        }
        catch (SqliteException ex)
        {
            return ToolExecutionResult.FromError(ex.Message);
        }
    }
}
```

- [ ] **Step 4: Run tests + build**

Run: `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj --filter "QueryCommandExecutorTests" -v q` → PASS
Run: `dotnet build CallGraph.csproj -v q` → succeeds.

- [ ] **Step 5: Commit**

```bash
git add Cli/QueryCommandExecutor.cs tests/CallGraph.Tests/Cli/QueryCommandExecutorTests.cs
git commit -m "Add read-only SQL query command executor"
```

---

### Task 3: Parser support for `query` + `--clear`/lifecycle, drop `--watch`

Teach the CLI parser to accept `callgraph query "<SQL>"` (positional SQL) and drop `--watch`. Do NOT yet wire query into Program.cs (Task 4 rewires the entrypoint).

**Files:**
- Modify: `Cli/Bootstrap/CliCommandLine.cs` (`TryParse`, `PrintUsage`), `CliOptions`/`NormalizedLifecycleOptions` records
- Modify: `Cli/Bootstrap/LifecycleCommandRunner.cs` (`NormalizeLifecycleOptions`, remove watch branches)
- Test: `tests/CallGraph.Tests/Cli/CliCommandLineTests.cs` (add cases; create file if absent)

**Interfaces:**
- Produces: parsing `["query", "SELECT 1"]` yields a `ToolCommand` named `query` whose SQL is retrievable. Store the SQL in `ToolCommand.Options` under key `sql` (reuse existing `ToolCommand(name, options)` shape) so downstream code reads `tool.Options["sql"]`.

- [ ] **Step 1: Write the failing test**

```csharp
using CallGraph.Cli;
using Xunit;

namespace CallGraph.Tests.Cli;

public sealed class CliCommandLineTests
{
    [Fact]
    public void Parse_Query_CapturesPositionalSql()
    {
        Assert.True(CliCommandLine.TryParse(new[] { "query", "SELECT * FROM Methods" }, out var opts, out var err));
        Assert.Null(err);
        Assert.NotNull(opts.ToolCommand);
        Assert.Equal("query", opts.ToolCommand!.Name);
        Assert.Equal("SELECT * FROM Methods", opts.ToolCommand.Options["sql"]);
    }

    [Fact]
    public void Parse_UnknownWatchFlag_IsRejected()
    {
        Assert.False(CliCommandLine.TryParse(new[] { "--watch" }, out _, out var err));
        Assert.NotNull(err);
    }
}
```

Note: `CliCommandLine`/`CliOptions`/`ToolCommand` are `internal`, but the CallGraph assembly already exposes internals to the test project (`CallGraph.csproj:40-42`, `InternalsVisibleTo("CallGraph.Tests")`), so no extra setup is needed.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj --filter "CliCommandLineTests" -v q`
Expected: FAIL — `query`'s positional SQL currently errors with "Unexpected token", and `--watch` currently parses successfully.

- [ ] **Step 3: Special-case `query` in `TryParse`**

In `Cli/Bootstrap/CliCommandLine.cs`, inside `TryParse`, after `NormalizeToolCommandName`, add before the generic `TryParseToolOptions` call:

```csharp
if (string.Equals(commandName, "query", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 2 || IsOption(args[1]))
    {
        options = new CliOptions(null, false, null, false, null, false, null);
        error = "query requires a SQL statement: callgraph query \"<SQL>\"";
        return false;
    }

    var sqlOptions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["sql"] = args[1] };
    options = new CliOptions(null, false, null, false, null, false, new ToolCommand("query", sqlOptions));
    error = null;
    return true;
}
```

- [ ] **Step 4: Remove `--watch` from lifecycle parsing**

- In `CliCommandLine.TryParse`, delete the `case "--watch":` block (lines 110-117) and the `watchEnabled`/`watchPath` locals. **Keep the `CliOptions` record shape unchanged** (7 positional params) to avoid touching every call site; always pass `false, null` for the `WatchEnabled`/`WatchPath` positions. `--watch` now falls through to the `default:` case and is reported as an unknown argument (this is what the Task 3 Step 1 test asserts).
- In `Cli/Bootstrap/LifecycleCommandRunner.cs`, `NormalizeLifecycleOptions`: delete the `--watch` handling and always set `WatchEnabled = false`; delete `EnsureWatchingAllIndexedSolutionsAsync` and the `--watch` branch in `RunLifecycleAsync` (the watcher registry resolution at ~L29 and branches at ~L34-46, L98-110).

- [ ] **Step 5: Update `PrintUsage`**

Replace the usage text in `CliCommandLine.PrintUsage` with only:

```
CallGraph CLI

Usage:
  callgraph --index <solution.sln>
  callgraph --reindex [solution.sln]
  callgraph --clear
  callgraph query "<SQL>"
  callgraph analyze --filepath <file.cs> [--method <name>] [--depth <n>] [--direction <inbound|outbound|bi-directional>] [--visibility <external|internal>] [--solutionPath <path>] [--solutionId <id>]

Notes:
  - query runs read-only SQL against the indexed SQLite database and prints tab-separated rows.
  - analyze traverses the indexed call graph; filePath must be an absolute .cs path.
```

- [ ] **Step 6: Run tests + build**

Run: `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj --filter "CliCommandLineTests" -v q` → PASS.
Build will still fail if `LifecycleCommandRunner` references removed watcher members — that is expected and finished in Task 5. If needed to keep this task green, temporarily leave the watcher-registry field but stop calling watch branches; otherwise fold Step 4's lifecycle edits into Task 5. **Decision: keep watch-removal edits to `LifecycleCommandRunner` minimal here (parsing only) and complete the watcher-registry removal in Task 5.** Verify: `dotnet build CallGraph.csproj -v q` succeeds.

- [ ] **Step 7: Commit**

```bash
git add Cli/Bootstrap/CliCommandLine.cs Cli/Bootstrap/LifecycleCommandRunner.cs tests/CallGraph.Tests/Cli/CliCommandLineTests.cs
git commit -m "Parse query command and drop --watch flag"
```

---

### Task 4: Rewire Program.cs + trim ToolCommandExecutor to analyze + query

Route only lifecycle (index/reindex/clear) and the two tool commands locally; remove all daemon dispatch. Trim the executor switch to `analyze` and `query`, and fix the eager-resolve block that force-resolves soon-to-be-deleted services.

**Files:**
- Modify: `Program.cs` (remove daemon branches; keep lifecycle + `RunToolLocallyAsync`)
- Modify: `Cli/ToolCommandExecutor.cs` (switch, `SupportedCommands`, eager-resolve block, `using`s)
- Test: `tests/CallGraph.Tests/Cli/ToolCommandExecutorTests.cs` (add a `query` dispatch case; trim removed-command cases)

**Interfaces:**
- Consumes: `QueryCommandExecutor.ExecuteAsync` (Task 2), `IndexStoreOptions.DatabasePath` via DI (`IOptions<IndexStoreOptions>`).
- Produces: `ExecuteAsync` handles only `"analyze"` and `"query"`.

- [ ] **Step 1: Trim `Program.Main`**

Replace the `if (options.ToolCommand is not null)` block (lines 27-55) with:

```csharp
if (options.ToolCommand is not null)
    return await RunToolLocallyAsync(args, options.ToolCommand).ConfigureAwait(false);
```

Delete the daemon reindex shortcut (lines 64-70) so lifecycle always runs locally:

```csharp
var normalized = LifecycleCommandRunner.NormalizeLifecycleOptions(options);
if (normalized.Error is not null)
{
    CliCommandLine.PrintUsage(normalized.Error);
    return 1;
}

return await LifecycleCommandRunner.RunLifecycleAsync(args, normalized, CreateHostBuilder).ConfigureAwait(false);
```

Remove the now-unused `using` for daemon types if any.

- [ ] **Step 2: Trim the executor entrypoint**

In `Cli/ToolCommandExecutor.cs`:
- Replace `SupportedCommands` (L22-35) with `{ "analyze", "query" }`.
- Delete `DaemonPreferredCommands` (L37-48) — verify no remaining reference (it was read by `DaemonCommandRunner.ShouldUseDaemon`, deleted in Task 5; if a reference remains, delete it there too).
- Delete the eager-resolve block (L74-79) and replace with just: `var graphAnalyzer = _services.GetRequiredService<IGraphAnalyzer>();`
- Delete the switch cases: `install`, `rewrite`, `reindex`, `list-solutions`, `search-file`, `search-method`, `list-methods`, `get-method-source`, `list-unused`, `list-warnings`, and all their private helper methods and fields (`WarningDiagnosticsCache*`, list-methods Roslyn helpers using `CallableSyntax`, etc.).
- Add a `query` case:

```csharp
case "query":
{
    var sql = CliInputHelpers.TryGetString(tool.Options, "sql");
    var dbPath = _services.GetRequiredService<IOptions<IndexStoreOptions>>().Value.DatabasePath;
    return await QueryCommandExecutor.ExecuteAsync(sql ?? string.Empty, dbPath, cancellationToken).ConfigureAwait(false);
}
```

- Prune `using`s to remove `CallGraph.Core.Diagnostics`, `CallGraph.Core.Extraction`, `CallGraph.Core.Search`, and unused Roslyn `using`s; add `using Microsoft.Extensions.Options;`.

- [ ] **Step 3: Add executor dispatch test**

```csharp
[Fact]
public async Task Execute_UnknownCommand_ReturnsError()
{
    // build a minimal ServiceProvider with IGraphAnalyzer + IOptions<IndexStoreOptions>
    // (follow the existing ToolCommandExecutorTests setup pattern), then:
    var executor = new ToolCommandExecutor(services, indexStore);
    var result = await executor.ExecuteAsync(new ToolCommand("search-file", new Dictionary<string, string?>()), CancellationToken.None);
    Assert.NotEqual(0, result.ExitCode); // removed command no longer supported
}
```
Follow the existing `ToolCommandExecutorTests` DI-setup helper; delete existing test cases that exercise removed commands.

- [ ] **Step 4: Build + test**

Run: `dotnet build CallGraph.csproj -v q` → succeeds (daemon files still exist but are now unreferenced; they are deleted in Task 5).
Run: `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj --filter "ToolCommandExecutorTests" -v q` → PASS.

- [ ] **Step 5: Commit**

```bash
git add Program.cs Cli/ToolCommandExecutor.cs tests/CallGraph.Tests/Cli/ToolCommandExecutorTests.cs
git commit -m "Route only analyze and query commands; drop daemon dispatch"
```

---

### Task 5: Delete daemon + watcher subsystems

**Files:**
- Delete: `Cli/Bootstrap/DaemonCommandRunner.cs`, `Cli/DaemonProtocol.cs`, `Core/Watching/` (all: `ISolutionWatcherRegistry.cs`, `SolutionWatcher.cs`, `SolutionWatcherHost.cs`)
- Modify: `Hosting/CallGraphComposition.cs` (remove L69-70 watcher registrations and L75 `AddHostedService(SolutionWatcherHost)`)
- Modify: `Cli/Bootstrap/LifecycleCommandRunner.cs` (remove residual watcher-registry field/usages from Task 3)
- Delete: any tests under `tests/CallGraph.Tests/` targeting the daemon or watcher (e.g. `*Daemon*`, `*Watcher*`).

- [ ] **Step 1: Delete files** (the `rm` list above).
- [ ] **Step 2: Remove watcher DI** in `CallGraphComposition.AddCallGraphCore` — delete the `SolutionWatcherHost`/`ISolutionWatcherRegistry` registrations and the watcher hosted service; keep `AddHostedService<IndexJobRunner>()`.
- [ ] **Step 3: Remove watcher test files.**
- [ ] **Step 4: Build + test**

Run: `dotnet build CallGraph.csproj -v q` → succeeds.
Run: `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj -v q` → PASS (whole suite; a full run is warranted after a subsystem delete).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Remove daemon and file-system watcher subsystems"
```

---

### Task 6: Delete semantic search subsystem + prune search query paths

**Files:**
- Delete: `Core/Search/` (all four files)
- Modify: `Hosting/CallGraphComposition.cs` (remove `HybridMethodSearchOptions`, `LocalBgeOptions`, `ISemanticEmbedder`, `IHybridMethodSearchService` registrations — L41-42, L55-56)
- Modify: `Core/Indexing/IIndexStore.cs` + `SqliteIndexStore.cs` (remove `SearchFilesAsync`, `SearchMethodsAsync`, `ListMethodsAsync` — the methods that return `SearchFileMatch`/`SearchMethodMatch`; keep `LoadAsync`, `ListSolutionsAsync`, `GetMethodAsync`, `GetEdgesAsync`, and all write/index methods)
- Delete: `Contracts/SearchRequests.cs`, `Contracts/SearchResponses.cs`
- Delete: search-related tests under `tests/CallGraph.Tests/`.

- [ ] **Step 1: Delete `Core/Search/` and `Contracts/Search*.cs`.**
- [ ] **Step 2: Remove search DI registrations** in `CallGraphComposition`.
- [ ] **Step 3: Remove the three search query methods** from `IIndexStore` and `SqliteIndexStore`. Before deleting each, confirm no surviving caller with: `grep -rn "SearchFilesAsync\|SearchMethodsAsync\|ListMethodsAsync" Core Cli Hosting Program.cs`. (Only removed-command code and tests should match.)
- [ ] **Step 4: Delete search test files.**
- [ ] **Step 5: Build + test**

Run: `dotnet build CallGraph.csproj -v q` → succeeds.
Run: `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj -v q` → PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Remove semantic search subsystem and search query methods"
```

---

### Task 7: Delete diagnostics, extraction, install/rewrite, dead contracts; trim analyze-shared output

**Files:**
- Delete: `Core/Diagnostics/` (all), `Core/Extraction/` (all), `Cli/InstallCommandRunner.cs`, `Cli/CommandRewriteEngine.cs`
- Delete: `Contracts/DiagnosticModels.cs`, `Contracts/DiagnosticRequests.cs`, `Contracts/ApiProblemDetails.cs`, `Contracts/McpErrorCodes.cs`
- Modify: `Hosting/CallGraphComposition.cs` (remove `DiagnosticCollectorOptions` L40, `IDiagnosticCollector` L64, `IMethodSourceExtractor` L65)
- Modify (trim to analyze-only members): `Contracts/ToolResponses.cs` (keep `AnalyzeToolResponse`/`AnalyzeMethodToolRow`/`AnalyzeCallToolRow`; remove `SearchFile*`/`SearchMethod*`/`Diagnostic*` records), `Core/Output/ToolResponseMapper.cs` (keep `ToAnalyzeResponse`; remove `ToSearchFileResponse`/`ToSearchMethodResponse`/`ToDiagnosticResponse`), `Core/Output/ToolTextFormatter.cs` (keep `FormatAnalyze`; remove `FormatSearchFiles`/`FormatSearchMethods`)
- Modify: `Core/Solutions/` — if `ISolutionContextCache`/`SolutionContextCache` is now used only by deleted diagnostics, remove it and its DI registration (L50); confirm with `grep -rn "ISolutionContextCache\|SolutionContextCache" Core Cli Hosting`.
- Delete: diagnostics/extraction/install/rewrite tests.

- [ ] **Step 1: Delete the files/dirs listed.**
- [ ] **Step 2: Remove DI registrations** for diagnostics/extraction (and `ISolutionContextCache` if now dead).
- [ ] **Step 3: Trim the three analyze-shared files** to only the analyze members (verify each removed member has no surviving caller via grep first).
- [ ] **Step 4: Delete corresponding test files.**
- [ ] **Step 5: Build + test**

Run: `dotnet build CallGraph.csproj -v q` → succeeds.
Run: `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj -v q` → PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Remove diagnostics, extraction, install/rewrite, and dead contracts"
```

---

### Task 8: Trim the project file — ONNX, models bundle, extra template dirs

**Files:**
- Modify: `CallGraph.csproj`

- [ ] **Step 1: Remove `Microsoft.ML.OnnxRuntime`** PackageReference (line 36).
- [ ] **Step 2: Remove the `models\bge-small-en-v1.5\**\*` Content item** (lines 54-58).
- [ ] **Step 3: Remove the four extra template Content items** — `_codex`, `_cursor`, `_copilot`, `_opencode` (lines 64-75). Keep the `_claude\**\*` item (lines 61-63).
- [ ] **Step 4: Build**

Run: `dotnet build CallGraph.csproj -v q` → succeeds (no more ONNX reference; indexing/analyze/query unaffected since nothing else uses OnnxRuntime).

- [ ] **Step 5: Commit**

```bash
git add CallGraph.csproj
git commit -m "Trim ONNX package and bundled model/template content"
```

---

### Task 9: Slim skills, templates, scripts, and deploy

**Files:**
- Delete dirs: `_codex/`, `_cursor/`, `_copilot/`, `_opencode/`, `models/`
- Delete: `scripts/bootstrap-bge-small-en-v1.5.sh`
- Delete in `_claude/`: `hooks/callgraph-rewrite.sh`, `agents/callgraph-haiku.md`, and every skill dir except `callgraph-analyze-callgraph/` (i.e. remove `callgraph-get-method-source`, `callgraph-list-methods`, `callgraph-list-unused`, `callgraph-list-warnings`, `callgraph-search-file`, `callgraph-search-method`, `callgraph-sequence-diagram`, `pr2-deep-pr-review`)
- Create: `_claude/skills/callgraph-sql/SKILL.md`
- Rewrite: `_claude/skills/callgraph-analyze-callgraph/SKILL.md`, `_claude/CLAUDE.md`
- Modify: `deploy.sh` (copy only `_claude`; remove the four other copies and the PreToolUse hook wiring), `scripts/clean-distributables.sh` (reduce the dir list to `_claude`)

- [ ] **Step 1: Delete the dirs/files listed above.**

- [ ] **Step 2: Rewrite `_claude/skills/callgraph-analyze-callgraph/SKILL.md`** — drop `agent: callgraph-haiku` and `context: fork` from frontmatter; remove the daemon/`--no-daemon` retry policy; replace the discovery steps that referenced `search-file`/`list-methods`/`search-method`/`get-method-source` with `callgraph query` SQL equivalents + direct file reads; keep the analyze flags, visibility/depth guidance, error handling, and the `M`/`C` row output format.

- [ ] **Step 3: Create `_claude/skills/callgraph-sql/SKILL.md`** documenting: the `callgraph query "<SQL>"` command; the tab-separated, read-only output contract; the full schema (Solutions, Projects, Files, Methods, Edges, SolutionAliases, SolutionSnapshots with columns); and worked example queries:

```
-- files by name
callgraph query "SELECT Path FROM Files WHERE Path LIKE '%Controller.cs'"
-- methods by name
callgraph query "SELECT Display, FilePath, StartLine FROM Methods WHERE Display LIKE '%Login%'"
-- methods in a type
callgraph query "SELECT Display, StartLine FROM Methods WHERE ContainingType='FooService' ORDER BY StartLine"
-- direct callers of a method (one hop)
callgraph query "SELECT m.Display FROM Edges e JOIN Methods m ON m.Key=e.FromKey WHERE e.ToKey='<methodKey>' AND e.Direction='outbound'"
```
Add a note: for multi-hop / recursive call-graph traversal use `callgraph analyze`, not hand-written recursive SQL.

- [ ] **Step 4: Rewrite `_claude/CLAUDE.md`** to a minimal note: CallGraph provides `callgraph query` (SQL over the indexed DB) and `callgraph analyze` (call-graph traversal); point at the two skills; remove all daemon/rewrite/discovery-command policy.

- [ ] **Step 5: Update `deploy.sh` and `scripts/clean-distributables.sh`** to reference only `_claude` and drop the hook wiring / bootstrap-model steps.

- [ ] **Step 6: Verify the skills reference only surviving commands**

Run: `grep -rn "search-file\|search-method\|list-methods\|get-method-source\|list-unused\|list-warnings\|--no-daemon\|serve\|--watch" _claude/`
Expected: no matches.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Reduce bundled skills to callgraph-sql and callgraph-analyze; trim deploy"
```

---

### Task 10: Update documentation

**Files:**
- Modify: `README.md`, `QUICKSTART.md`, `CLAUDE.md` (project root)

- [ ] **Step 1: Update all three** to describe only `--index` / `--reindex` / `--clear` / `query "<SQL>"` / `analyze`, the DB schema (7 tables), and the two skills. Remove references to search/list/diagnostics commands, the daemon (serve/status/stop), `--watch`, semantic search, install, and the four removed template dirs. Update the root `CLAUDE.md` "Analysis Commands" and "Key Behavior" sections accordingly.

- [ ] **Step 2: Verify**

Run: `grep -rn "search-method\|list-methods\|list-unused\|list-warnings\|serve\|--watch\|semantic" README.md QUICKSTART.md CLAUDE.md`
Expected: no stale matches.

- [ ] **Step 3: Commit**

```bash
git add README.md QUICKSTART.md CLAUDE.md
git commit -m "Update docs for slimmed index/reindex/query/analyze surface"
```

---

## Verification (end-to-end)

- [ ] **Build:** `dotnet build CallGraph.csproj -v q` → succeeds with no reference to removed namespaces or the ONNX package.
- [ ] **Full test suite:** `dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj -v q` → PASS.
- [ ] **Index a real solution:** `dotnet run --project CallGraph.csproj -- --index "<abs>/SomeSolution.sln"` → completes; DB exists at `<LocalApplicationData>/CallGraph/index.db`.
- [ ] **Query each table returns rows:** `dotnet run --project CallGraph.csproj -- query "SELECT COUNT(*) AS n FROM Methods"` → prints `n\n<count>`; repeat for `Files`, `Edges`, `Solutions`.
- [ ] **Query rejects writes:** `dotnet run --project CallGraph.csproj -- query "DELETE FROM Methods"` → non-zero exit + read-only error on stderr.
- [ ] **Analyze runs one-shot (no daemon):** `dotnet run --project CallGraph.csproj -- analyze --filepath "<abs>/SomeFile.cs" --depth 1` → prints `M`/`C` rows.
- [ ] **No daemon spawned:** confirm no `serve` process starts and no named pipe is created during the above.
- [ ] **Skills clean:** `grep -rn` over `_claude/` finds no removed-command references (Task 9 Step 6).
