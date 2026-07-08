using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Git;
using CallGraph.Core.Indexing;
using CallGraph.Core.Projects;
using CallGraph.Core.Solutions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CallGraph.Tests;

public sealed class IndexingPipelineIncrementalReindexTests
{
    [Fact]
    public async Task Reindex_NoChanges_SkipsFullIndexing()
    {
        var tempDir = CreateTempDir();
        try
        {
            var solutionPath = CreateFile(tempDir, "test.sln", "Microsoft Visual Studio Solution File");
            var projectPath = CreateFile(tempDir, "test.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var codeFilePath = CreateFile(tempDir, "Foo.cs", "public class Foo { }");

            var currentWriteUtc = DateTime.UtcNow.AddMinutes(-2);
            File.SetLastWriteTimeUtc(solutionPath, currentWriteUtc);
            File.SetLastWriteTimeUtc(projectPath, currentWriteUtc);
            File.SetLastWriteTimeUtc(codeFilePath, currentWriteUtc);

            var indexStore = new StubIndexStore(
                indexedAtUtc: DateTime.UtcNow.AddMinutes(2),
                projectPaths: new[] { projectPath },
                indexedFiles: new[] { new IndexedFileInfo(codeFilePath, currentWriteUtc) });

            var pipeline = CreatePipeline(indexStore, new StubFileIndexer(), new StubGitRepositoryInspector());

            await pipeline.RunAsync(
                new IndexJobRequest("job-1", "solution-1", solutionPath, true, IsReindex: true),
                CancellationToken.None);

            Assert.Empty(indexStore.UpdateFileCalls);
            Assert.Empty(indexStore.RemoveFileCalls);
            Assert.False(indexStore.SaveCalled);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Reindex_ChangedFile_UsesIncrementalUpdate()
    {
        var tempDir = CreateTempDir();
        try
        {
            var solutionPath = CreateFile(tempDir, "test.sln", "Microsoft Visual Studio Solution File");
            var projectPath = CreateFile(tempDir, "test.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var codeFilePath = CreateFile(tempDir, "Foo.cs", "public class Foo { }");

            var oldWriteUtc = DateTime.UtcNow.AddMinutes(-5);
            var newWriteUtc = DateTime.UtcNow.AddMinutes(-1);
            File.SetLastWriteTimeUtc(solutionPath, oldWriteUtc);
            File.SetLastWriteTimeUtc(projectPath, oldWriteUtc);
            File.SetLastWriteTimeUtc(codeFilePath, newWriteUtc);

            var indexStore = new StubIndexStore(
                indexedAtUtc: DateTime.UtcNow.AddMinutes(2),
                projectPaths: new[] { projectPath },
                indexedFiles: new[] { new IndexedFileInfo(codeFilePath, oldWriteUtc) });

            var fileIndexer = new StubFileIndexer();
            fileIndexer.IndexedResults[Path.GetFullPath(codeFilePath)] = new FileIndex
            {
                FilePath = codeFilePath,
                Nodes = new List<Node>
                {
                    new()
                    {
                        Id = "M:Foo.Bar()",
                        Kind = "method",
                        Display = "Foo.Bar()",
                        FilePath = codeFilePath
                    }
                },
                Edges = new List<Edge>()
            };

            var pipeline = CreatePipeline(indexStore, fileIndexer, new StubGitRepositoryInspector());

            await pipeline.RunAsync(
                new IndexJobRequest("job-1", "solution-1", solutionPath, true, IsReindex: true),
                CancellationToken.None);

            Assert.Single(indexStore.UpdateFileCalls);
            Assert.Equal(Path.GetFullPath(codeFilePath), Path.GetFullPath(indexStore.UpdateFileCalls[0].FilePath));
            Assert.Empty(indexStore.RemoveFileCalls);
            Assert.False(indexStore.SaveCalled);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Reindex_OldUnindexedFiles_DoNotTriggerFullFallback()
    {
        var tempDir = CreateTempDir();
        try
        {
            var solutionPath = CreateFile(tempDir, "test.sln", "Microsoft Visual Studio Solution File");
            var projectPath = CreateFile(tempDir, "test.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var indexedFilePath = CreateFile(tempDir, "Indexed.cs", "public class Indexed { public void Run() {} }");

            var indexedAtUtc = DateTime.UtcNow;

            var oldUnindexedFiles = Enumerable.Range(0, 80)
                .Select(i => CreateFile(tempDir, $"Old/NoMethods{i}.cs", "namespace N; public class C { }"))
                .ToList();

            var oldWriteUtc = indexedAtUtc.AddMinutes(-10);
            File.SetLastWriteTimeUtc(solutionPath, oldWriteUtc);
            File.SetLastWriteTimeUtc(projectPath, oldWriteUtc);
            File.SetLastWriteTimeUtc(indexedFilePath, oldWriteUtc);
            foreach (var file in oldUnindexedFiles)
                File.SetLastWriteTimeUtc(file, oldWriteUtc);

            var indexStore = new StubIndexStore(
                indexedAtUtc: indexedAtUtc,
                projectPaths: new[] { projectPath },
                indexedFiles: new[] { new IndexedFileInfo(indexedFilePath, oldWriteUtc) });

            var pipeline = CreatePipeline(indexStore, new StubFileIndexer(), new StubGitRepositoryInspector());

            await pipeline.RunAsync(
                new IndexJobRequest("job-1", "solution-1", solutionPath, true, IsReindex: true),
                CancellationToken.None);

            Assert.Empty(indexStore.UpdateFileCalls);
            Assert.Empty(indexStore.RemoveFileCalls);
            Assert.False(indexStore.SaveCalled);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Reindex_GitCommitDiff_UsesIncrementalUpdateAndUpdatesMetadata()
    {
        var tempDir = CreateTempDir();
        try
        {
            var solutionPath = CreateFile(tempDir, "test.sln", "Microsoft Visual Studio Solution File");
            var codeFilePath = CreateFile(tempDir, "Foo.cs", "public class Foo { }");
            var indexedAtUtc = DateTime.UtcNow.AddMinutes(-10);

            var indexStore = new StubIndexStore(
                indexedAtUtc: indexedAtUtc,
                projectPaths: Array.Empty<string>(),
                indexedFiles: new[] { new IndexedFileInfo(codeFilePath, indexedAtUtc.AddMinutes(-1)) },
                indexedHeadCommit: "commit-old");

            var fileIndexer = new StubFileIndexer();
            fileIndexer.IndexedResults[Path.GetFullPath(codeFilePath)] = new FileIndex
            {
                FilePath = codeFilePath,
                Nodes = [new Node { Id = "M:Foo.Bar()", Kind = "method", Display = "Foo.Bar()", FilePath = codeFilePath }],
                Edges = []
            };

            var gitInspector = new StubGitRepositoryInspector
            {
                RepositoryInfo = new GitRepositoryInfo(tempDir, Path.Combine(tempDir, ".git"), "commit-new")
            };
            gitInspector.CommitChanges.Add(new GitPathChange("Foo.cs", GitPathChangeKind.Modified));

            var pipeline = CreatePipeline(indexStore, fileIndexer, gitInspector);

            await pipeline.RunAsync(
                new IndexJobRequest("job-1", "solution-1", solutionPath, true, IsReindex: true),
                CancellationToken.None);

            Assert.Single(indexStore.UpdateFileCalls);
            Assert.Empty(indexStore.RemoveFileCalls);
            Assert.Single(indexStore.MetadataUpdates);
            Assert.Equal("commit-new", indexStore.MetadataUpdates[0].HeadCommit);
            Assert.Single(indexStore.SavedSnapshots);
            Assert.Equal("commit-new", indexStore.SavedSnapshots[0]);
            Assert.False(indexStore.SaveCalled);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Reindex_GitMetadataChange_FallsBackToFullIndex()
    {
        var tempDir = CreateTempDir();
        try
        {
            var solutionPath = CreateFile(tempDir, "test.sln", "Microsoft Visual Studio Solution File");
            var codeFilePath = CreateFile(tempDir, "Foo.cs", "public class Foo { }");

            var indexStore = new StubIndexStore(
                indexedAtUtc: DateTime.UtcNow.AddMinutes(-10),
                projectPaths: Array.Empty<string>(),
                indexedFiles: new[] { new IndexedFileInfo(codeFilePath, DateTime.UtcNow.AddMinutes(-11)) },
                indexedHeadCommit: "commit-old");

            var gitInspector = new StubGitRepositoryInspector
            {
                RepositoryInfo = new GitRepositoryInfo(tempDir, Path.Combine(tempDir, ".git"), "commit-new")
            };
            gitInspector.CommitChanges.Add(new GitPathChange("test.csproj", GitPathChangeKind.Modified));

            var pipeline = CreatePipeline(indexStore, new StubFileIndexer(), gitInspector);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await pipeline.RunAsync(
                    new IndexJobRequest("job-1", "solution-1", solutionPath, true, IsReindex: true),
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Reindex_GitCommitSwitchWithoutCodeChanges_OnlyUpdatesMetadata()
    {
        var tempDir = CreateTempDir();
        try
        {
            var solutionPath = CreateFile(tempDir, "test.sln", "Microsoft Visual Studio Solution File");
            var codeFilePath = CreateFile(tempDir, "Foo.cs", "public class Foo { }");

            var indexStore = new StubIndexStore(
                indexedAtUtc: DateTime.UtcNow.AddMinutes(-10),
                projectPaths: Array.Empty<string>(),
                indexedFiles: new[] { new IndexedFileInfo(codeFilePath, DateTime.UtcNow.AddMinutes(-11)) },
                indexedHeadCommit: "commit-old");

            var gitInspector = new StubGitRepositoryInspector
            {
                RepositoryInfo = new GitRepositoryInfo(tempDir, Path.Combine(tempDir, ".git"), "commit-new")
            };
            gitInspector.CommitChanges.Add(new GitPathChange("README.md", GitPathChangeKind.Modified));

            var pipeline = CreatePipeline(indexStore, new StubFileIndexer(), gitInspector);

            await pipeline.RunAsync(
                new IndexJobRequest("job-1", "solution-1", solutionPath, true, IsReindex: true),
                CancellationToken.None);

            Assert.Empty(indexStore.UpdateFileCalls);
            Assert.Empty(indexStore.RemoveFileCalls);
            Assert.Single(indexStore.MetadataUpdates);
            Assert.Equal("commit-new", indexStore.MetadataUpdates[0].HeadCommit);
            Assert.Single(indexStore.SavedSnapshots);
            Assert.Equal("commit-new", indexStore.SavedSnapshots[0]);
            Assert.False(indexStore.SaveCalled);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Reindex_GitCommitSwitch_RestoresSnapshotWithoutDiffRebuild()
    {
        var tempDir = CreateTempDir();
        try
        {
            var solutionPath = CreateFile(tempDir, "test.sln", "Microsoft Visual Studio Solution File");
            var codeFilePath = CreateFile(tempDir, "Foo.cs", "public class Foo { }");

            var indexStore = new StubIndexStore(
                indexedAtUtc: DateTime.UtcNow.AddMinutes(-10),
                projectPaths: Array.Empty<string>(),
                indexedFiles: new[] { new IndexedFileInfo(codeFilePath, DateTime.UtcNow.AddMinutes(-11)) },
                indexedHeadCommit: "commit-old")
            {
                TryRestoreSnapshotResult = true
            };

            var gitInspector = new StubGitRepositoryInspector
            {
                RepositoryInfo = new GitRepositoryInfo(tempDir, Path.Combine(tempDir, ".git"), "commit-new")
            };

            var pipeline = CreatePipeline(indexStore, new StubFileIndexer(), gitInspector);

            await pipeline.RunAsync(
                new IndexJobRequest("job-1", "solution-1", solutionPath, true, IsReindex: true),
                CancellationToken.None);

            Assert.Equal(1, indexStore.TryRestoreSnapshotCallCount);
            Assert.Equal(0, gitInspector.CommitChangesCallCount);
            Assert.Empty(indexStore.UpdateFileCalls);
            Assert.Empty(indexStore.RemoveFileCalls);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static IndexingPipeline CreatePipeline(
        StubIndexStore indexStore,
        StubFileIndexer fileIndexer,
        StubGitRepositoryInspector gitRepositoryInspector)
        => new(
            new ThrowingSolutionLoader(),
            new ThrowingProjectIndexer(),
            fileIndexer,
            new ThrowingGraphBuilder(),
            indexStore,
            gitRepositoryInspector,
            NullLogger<IndexingPipeline>.Instance);

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"callgraph-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateFile(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath);
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        File.WriteAllText(path, content);
        return path;
    }

    private sealed class StubFileIndexer : IFileIndexer
    {
        public Dictionary<string, FileIndex> IndexedResults { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<FileIndex?> IndexFileAsync(
            string solutionPath,
            string filePath,
            bool slnOnly,
            CancellationToken cancellationToken)
            => Task.FromResult(IndexedResults.TryGetValue(Path.GetFullPath(filePath), out var result) ? result : null);

        public Task<IReadOnlyList<FileIndex>> IndexFilesAsync(
            string solutionPath,
            IReadOnlyList<string> filePaths,
            bool slnOnly,
            CancellationToken cancellationToken)
        {
            var results = filePaths
                .Select(path => Path.GetFullPath(path))
                .Where(path => IndexedResults.TryGetValue(path, out _))
                .Select(path => IndexedResults[path])
                .ToList();
            return Task.FromResult<IReadOnlyList<FileIndex>>(results);
        }
    }

    private sealed class StubGitRepositoryInspector : IGitRepositoryInspector
    {
        public GitRepositoryInfo? RepositoryInfo { get; set; }

        public List<GitPathChange> CommitChanges { get; } = new();

        public List<GitPathChange> PendingChanges { get; } = new();

        public int CommitChangesCallCount { get; private set; }

        public Task<GitRepositoryInfo?> TryGetRepositoryInfoAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(RepositoryInfo);

        public Task<IReadOnlyList<GitPathChange>> GetCommitChangesAsync(
            string repositoryRoot,
            string fromCommit,
            string toCommit,
            CancellationToken cancellationToken)
        {
            CommitChangesCallCount++;
            return Task.FromResult<IReadOnlyList<GitPathChange>>(CommitChanges.ToList());
        }

        public Task<IReadOnlyList<GitPathChange>> GetPendingChangesAsync(
            string repositoryRoot,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GitPathChange>>(PendingChanges.ToList());
    }

    private sealed class StubIndexStore : IIndexStore
    {
        private readonly DateTime _indexedAtUtc;
        private readonly IReadOnlyList<string> _projectPaths;
        private readonly IReadOnlyList<IndexedFileInfo> _indexedFiles;
        private readonly string? _indexedHeadCommit;

        public StubIndexStore(
            DateTime indexedAtUtc,
            IReadOnlyList<string> projectPaths,
            IReadOnlyList<IndexedFileInfo> indexedFiles,
            string? indexedHeadCommit = null)
        {
            _indexedAtUtc = indexedAtUtc;
            _projectPaths = projectPaths;
            _indexedFiles = indexedFiles;
            _indexedHeadCommit = indexedHeadCommit;
        }

        public List<FileIndex> UpdateFileCalls { get; } = new();
        public List<string> RemoveFileCalls { get; } = new();
        public List<(DateTime IndexedAtUtc, string? HeadCommit)> MetadataUpdates { get; } = new();
        public List<string> SavedSnapshots { get; } = new();
        public bool SaveCalled { get; private set; }
        public bool TryRestoreSnapshotResult { get; set; }
        public int TryRestoreSnapshotCallCount { get; private set; }

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(SolutionIndex index, CancellationToken cancellationToken)
        {
            SaveCalled = true;
            return Task.CompletedTask;
        }

        public Task<SolutionIndex?> LoadAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<SolutionIndex?>(null);

        public Task<IReadOnlyList<SolutionInfo>> ListSolutionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SolutionInfo>>(Array.Empty<SolutionInfo>());

        public Task<SolutionInfo?> GetSolutionByPathAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<SolutionInfo?>(new SolutionInfo("solution-1", Path.GetFullPath(solutionPath), true));

        public Task<DateTime?> GetIndexedAtUtcAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<DateTime?>(_indexedAtUtc);

        public Task<string?> GetIndexedHeadCommitAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult(_indexedHeadCommit);

        public Task UpdateSolutionMetadataAsync(
            string solutionPath,
            DateTime indexedAtUtc,
            string? headCommit,
            CancellationToken cancellationToken)
        {
            MetadataUpdates.Add((indexedAtUtc, headCommit));
            return Task.CompletedTask;
        }

        public Task<bool> TryRestoreSnapshotAsync(string solutionPath, string headCommit, CancellationToken cancellationToken)
        {
            TryRestoreSnapshotCallCount++;
            return Task.FromResult(TryRestoreSnapshotResult);
        }

        public Task SaveSnapshotAsync(string solutionPath, string headCommit, CancellationToken cancellationToken)
        {
            SavedSnapshots.Add(headCommit);
            return Task.CompletedTask;
        }

        public Task<SolutionInfo?> GetSolutionByIdAsync(string solutionId, CancellationToken cancellationToken)
            => Task.FromResult<SolutionInfo?>(null);

        public Task<IReadOnlyList<IndexedFileInfo>> ListFilesAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult(_indexedFiles);

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

        public Task<Node?> GetMethodAsync(string solutionPath, string methodKey, CancellationToken cancellationToken)
            => Task.FromResult<Node?>(null);

        public Task<IReadOnlyList<Edge>> GetEdgesAsync(string solutionPath, string methodKey, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Edge>>(Array.Empty<Edge>());

        public Task UpdateFileAsync(string solutionPath, FileIndex update, CancellationToken cancellationToken)
        {
            UpdateFileCalls.Add(update);
            return Task.CompletedTask;
        }

        public Task RemoveFileAsync(string solutionPath, string filePath, CancellationToken cancellationToken)
        {
            RemoveFileCalls.Add(Path.GetFullPath(filePath));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSolutionLoader : ISolutionLoader
    {
        public Task<SolutionLoadContext> LoadAsync(string solutionPath, bool slnOnly, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Full indexing path should not run.");

        public Task<SolutionLoadContext> LoadProjectAsync(string projectPath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Not used.");
    }

    private sealed class ThrowingProjectIndexer : IProjectIndexer
    {
        public Task<IndexSession> IndexAsync(IReadOnlyList<Microsoft.CodeAnalysis.Project> projects, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Full indexing path should not run.");
    }

    private sealed class ThrowingGraphBuilder : IGraphBuilder
    {
        public SolutionIndex BuildIndex(
            string solutionId,
            string solutionPath,
            IndexSession session,
            bool slnOnly,
            DateTime? indexedAtUtc = null)
            => throw new InvalidOperationException("Full indexing path should not run.");

        public Graph BuildGraph(IndexSession session, HashSet<string> targets, int depth, string direction, string visibility)
            => throw new InvalidOperationException("Not used.");
    }
}
