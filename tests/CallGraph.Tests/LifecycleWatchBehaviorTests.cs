using System.Reflection;
using CallGraph;
using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using CallGraph.Core.Projects;
using CallGraph.Core.Solutions;
using CallGraph.Core.Watching;
using CallGraph.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CallGraph.Tests;

public sealed class LifecycleWatchBehaviorTests
{
    [Fact]
    public void NormalizeLifecycleOptions_WatchOnly_DoesNotImplicitlyIndex()
    {
        var assembly = typeof(Program).Assembly;
        var cliOptionsType = assembly.GetType("CallGraph.CliOptions", throwOnError: false);
        Assert.NotNull(cliOptionsType);

        var commandLineType = assembly.GetType("CallGraph.CliCommandLine", throwOnError: false);
        Assert.NotNull(commandLineType);

        var tryParseMethod = commandLineType!.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(tryParseMethod);

        object?[] parseArguments = { new[] { "--watch" }, null, null };
        var parsed = (bool)tryParseMethod!.Invoke(null, parseArguments)!;
        Assert.True(parsed);

        var cliOptions = parseArguments[1];
        Assert.NotNull(cliOptions);

        var lifecycleType = assembly.GetType("CallGraph.LifecycleCommandRunner", throwOnError: false);
        Assert.NotNull(lifecycleType);

        var normalizeMethod = lifecycleType!.GetMethod("NormalizeLifecycleOptions", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(normalizeMethod);

        var normalized = normalizeMethod!.Invoke(null, new[] { cliOptions! });
        Assert.NotNull(normalized);

        var normalizedType = normalized!.GetType();
        var action = normalizedType.GetProperty("Action")!.GetValue(normalized);
        var actionPath = normalizedType.GetProperty("ActionPath")!.GetValue(normalized);
        var watchEnabled = (bool)normalizedType.GetProperty("WatchEnabled")!.GetValue(normalized)!;

        Assert.Equal("None", action?.ToString());
        Assert.Null(actionPath);
        Assert.True(watchEnabled);
    }

    [Fact]
    public async Task EnsureWatchingAsync_SameRegistration_DoesNotRestartWatcher()
    {
        CallGraphComposition.EnsureMsBuildRegistered();

        var tempDir = Path.Combine(Path.GetTempPath(), $"callgraph-watch-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var indexStore = new SqliteIndexStore(Options.Create(new IndexStoreOptions
        {
            DatabasePath = Path.Combine(tempDir, "index.db")
        }));

        var host = new SolutionWatcherHost(
            NullLogger<SolutionWatcherHost>.Instance,
            new SolutionLoader(new ProjectFilter(), new SolutionFileParser()),
            indexStore,
            new InMemoryIndexJobStore(),
            new NoopSolutionIndexer());

        try
        {
            var solutionPath = GetTestSolutionPath();

            await host.EnsureWatchingAsync(solutionPath, slnOnly: true, CancellationToken.None);
            var firstWatcher = GetWatcherInstance(host);

            await host.EnsureWatchingAsync(solutionPath, slnOnly: true, CancellationToken.None);
            var secondWatcher = GetWatcherInstance(host);

            Assert.Same(firstWatcher, secondWatcher);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string GetTestSolutionPath()
        => Path.GetFullPath(
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "CallGraph.Tests", "TestAssets", "InterfaceCallE2E", "InterfaceCallE2E.sln"));

    private static object GetWatcherInstance(SolutionWatcherHost host)
    {
        var watchersField = typeof(SolutionWatcherHost).GetField("_watchers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(watchersField);

        var watchers = (System.Collections.IEnumerable?)watchersField!.GetValue(host);
        Assert.NotNull(watchers);

        var enumerator = watchers!.GetEnumerator();
        Assert.True(enumerator.MoveNext());

        var entry = enumerator.Current;
        Assert.NotNull(entry);

        var value = entry!.GetType().GetProperty("Value")!.GetValue(entry);
        Assert.NotNull(value);

        var watcher = value!.GetType().GetProperty("Watcher")!.GetValue(value);
        Assert.NotNull(watcher);
        return watcher!;
    }

    private sealed class NoopSolutionIndexer : ISolutionIndexer
    {
        public Task<IndexJobResponse> EnqueueIndexAsync(IndexRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new IndexJobResponse(Guid.NewGuid().ToString("N"), "noop"));

        public Task<IndexJobResponse> EnqueueReindexAsync(ReindexRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new IndexJobResponse(Guid.NewGuid().ToString("N"), "noop"));
    }
}
