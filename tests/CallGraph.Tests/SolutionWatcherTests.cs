using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using CallGraph.Core.Projects;
using CallGraph.Core.Solutions;
using CallGraph.Core.Watching;
using CallGraph.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

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

    // Helper classes for testing

    private sealed class InMemoryIndexStore : IIndexStore
    {
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
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

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
}
