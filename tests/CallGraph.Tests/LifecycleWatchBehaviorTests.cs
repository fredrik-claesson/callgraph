using System.Reflection;
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
