using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using CallGraph.Core.Projects;
using CallGraph.Core.Solutions;
using CallGraph.Core.Watching;
using CallGraph.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace CallGraph.Tests;

public sealed class SolutionWatcherTests
{
    [Fact]
    public async Task CanCreateAndDisposeWatcher()
    {
        // Arrange
        CallGraphComposition.EnsureMsBuildRegistered();

        var solutionPath = Path.GetFullPath(
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "CallGraph.Tests", "TestAssets", "InterfaceCallE2E", "InterfaceCallE2E.sln"));

        var solutionLoader = new SolutionLoader(
            new ProjectFilter(),
            new SolutionFileParser());

        var indexStore = new InMemoryIndexStore();
        var solutionIndexer = new InMemorySolutionIndexer();

        var watcher = new SolutionWatcher(
            solutionPath,
            slnOnly: true,
            solutionLoader,
            indexStore,
            solutionIndexer,
            NullLogger.Instance);

        // Act & Assert - should not throw
        await watcher.StartAsync(CancellationToken.None);
        watcher.Dispose();
    }

    [Fact]
    public void WatcherHandlesInvalidPath()
    {
        // Arrange
        var invalidPath = "C:\\NonExistent\\Solution.sln";

        var solutionLoader = new SolutionLoader(
            new ProjectFilter(),
            new SolutionFileParser());

        var indexStore = new InMemoryIndexStore();
        var solutionIndexer = new InMemorySolutionIndexer();

        var watcher = new SolutionWatcher(
            invalidPath,
            slnOnly: true,
            solutionLoader,
            indexStore,
            solutionIndexer,
            NullLogger.Instance);

        // Act & Assert - should not throw during construction
        Assert.NotNull(watcher);

        // Cleanup
        watcher.Dispose();
    }

    [Fact]
    public async Task StartAsync_WithIndexedProjectPaths_DoesNotBlockOnBackgroundSolutionLoad()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"callgraph-watch-start-{Guid.NewGuid():N}");
        var solutionPath = Path.Combine(tempDir, "Sample.sln");
        var projectDir = Path.Combine(tempDir, "src", "Sample");
        var projectPath = Path.Combine(projectDir, "Sample.csproj");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(solutionPath, string.Empty);
        await File.WriteAllTextAsync(projectPath, string.Empty);

        var blockingLoader = new BlockingSolutionLoader();
        var indexStore = new InMemoryIndexStore([projectPath]);
        var watcher = new SolutionWatcher(
            solutionPath,
            slnOnly: true,
            blockingLoader,
            indexStore,
            new InMemorySolutionIndexer(),
            NullLogger.Instance);

        try
        {
            var startTask = watcher.StartAsync(CancellationToken.None);
            var completed = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(startTask, completed);
            await startTask;

            var loadInvoked = await WaitUntilAsync(
                () => Volatile.Read(ref blockingLoader.LoadAsyncCallCount) > 0,
                timeout: TimeSpan.FromSeconds(2));
            Assert.True(loadInvoked);
        }
        finally
        {
            watcher.Dispose();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void OnChanged_IgnoresObjAndBinPaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"callgraph-watch-ignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var solutionPath = Path.Combine(tempDir, "Sample.sln");
        File.WriteAllText(solutionPath, string.Empty);

        var objDir = Path.Combine(tempDir, "src", "Sample", "obj", "Debug");
        Directory.CreateDirectory(objDir);
        var objFile = Path.Combine(objDir, "Generated.cs");
        File.WriteAllText(objFile, "class Generated {}");

        var sourceDir = Path.Combine(tempDir, "src", "Sample");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "Regular.cs");
        File.WriteAllText(sourceFile, "class Regular {}");

        var watcher = new SolutionWatcher(
            solutionPath,
            slnOnly: true,
            new ThrowingSolutionLoader(),
            new InMemoryIndexStore(),
            new InMemorySolutionIndexer(),
            NullLogger.Instance);

        try
        {
            InvokeOnChanged(watcher, objFile);
            Assert.Equal(0, GetPendingUpdateCount(watcher));

            InvokeOnChanged(watcher, sourceFile);
            Assert.Equal(1, GetPendingUpdateCount(watcher));
        }
        finally
        {
            watcher.Dispose();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // Helper classes for testing

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(20);
        }

        return condition();
    }

    private static void InvokeOnChanged(SolutionWatcher watcher, string filePath)
    {
        var onChanged = typeof(SolutionWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onChanged);

        var directory = Path.GetDirectoryName(filePath)!;
        var fileName = Path.GetFileName(filePath);
        onChanged!.Invoke(
            watcher,
            [watcher, new FileSystemEventArgs(WatcherChangeTypes.Changed, directory, fileName)]);
    }

    private static int GetPendingUpdateCount(SolutionWatcher watcher)
    {
        var pendingUpdatesField = typeof(SolutionWatcher).GetField("_pendingUpdates", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pendingUpdatesField);

        var pendingUpdates = pendingUpdatesField!.GetValue(watcher);
        Assert.NotNull(pendingUpdates);

        var countProperty = pendingUpdates!.GetType().GetProperty("Count");
        Assert.NotNull(countProperty);
        return (int)countProperty!.GetValue(pendingUpdates)!;
    }

    private sealed class InMemoryIndexStore : IIndexStore
    {
        private readonly IReadOnlyList<string> _projectPaths;

        public InMemoryIndexStore(IReadOnlyList<string>? projectPaths = null)
        {
            _projectPaths = projectPaths ?? Array.Empty<string>();
        }

        public Task ClearAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SaveAsync(SolutionIndex index, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<SolutionIndex?> LoadAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<SolutionIndex?>(null);

        public Task<IReadOnlyList<SolutionInfo>> ListSolutionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SolutionInfo>>(Array.Empty<SolutionInfo>());

        public Task<SolutionInfo?> GetSolutionByPathAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<SolutionInfo?>(null);

        public Task<DateTime?> GetIndexedAtUtcAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<DateTime?>(null);

        public Task<SolutionInfo?> GetSolutionByIdAsync(string solutionId, CancellationToken cancellationToken)
            => Task.FromResult<SolutionInfo?>(null);

        public Task<IReadOnlyList<IndexedFileInfo>> ListFilesAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<IndexedFileInfo>>(Array.Empty<IndexedFileInfo>());

        public Task<IReadOnlyList<string>> ListProjectPathsAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult(_projectPaths);

        public Task<IReadOnlyList<SolutionInfo>> FindSolutionsByFilePathAsync(string filePath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SolutionInfo>>(Array.Empty<SolutionInfo>());

        public Task<IReadOnlyList<SolutionFileMatch>> FindSolutionsByFilePathSuffixAsync(
            string relativeFilePath,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SolutionFileMatch>>(Array.Empty<SolutionFileMatch>());

        public Task<IReadOnlyList<SolutionProjectMatch>> FindProjectsByPathSuffixAsync(
            string relativeProjectPath,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SolutionProjectMatch>>(Array.Empty<SolutionProjectMatch>());

        public Task<IReadOnlyList<SearchFileMatch>> SearchFilesAsync(
            string pattern,
            bool useRegex,
            string? solutionPath,
            string? solutionId,
            string? folderPath,
            string? filePath,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SearchFileMatch>>(Array.Empty<SearchFileMatch>());

        public Task<IReadOnlyList<SearchMethodMatch>> SearchMethodsAsync(
            string pattern,
            bool useRegex,
            string? solutionPath,
            string? solutionId,
            string? folderPath,
            string? filePath,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SearchMethodMatch>>(Array.Empty<SearchMethodMatch>());

        public Task<IReadOnlyList<SearchMethodMatch>> ListMethodsAsync(
            string visibility,
            string? solutionPath,
            string? solutionId,
            string? folderPath,
            string? filePath,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SearchMethodMatch>>(Array.Empty<SearchMethodMatch>());

        public Task<Node?> GetMethodAsync(string solutionPath, string methodKey, CancellationToken cancellationToken)
            => Task.FromResult<Node?>(null);

        public Task<IReadOnlyList<Edge>> GetEdgesAsync(string solutionPath, string methodKey, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Edge>>(Array.Empty<Edge>());

        public Task UpdateFileAsync(string solutionPath, FileIndex update, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemoveFileAsync(string solutionPath, string filePath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class InMemorySolutionIndexer : ISolutionIndexer
    {
        public Task<IndexJobResponse> EnqueueIndexAsync(IndexRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new IndexJobResponse(Guid.NewGuid().ToString(), "test-solution-id"));

        public Task<IndexJobResponse> EnqueueReindexAsync(ReindexRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new IndexJobResponse(Guid.NewGuid().ToString(), "test-solution-id"));
    }

    private sealed class ThrowingSolutionLoader : ISolutionLoader
    {
        public Task<SolutionLoadContext> LoadAsync(string solutionPath, bool slnOnly, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Solution loader should not be called in this test.");

        public Task<SolutionLoadContext> LoadProjectAsync(string projectPath, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class BlockingSolutionLoader : ISolutionLoader
    {
        public int LoadAsyncCallCount;

        public async Task<SolutionLoadContext> LoadAsync(string solutionPath, bool slnOnly, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref LoadAsyncCallCount);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public Task<SolutionLoadContext> LoadProjectAsync(string projectPath, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
