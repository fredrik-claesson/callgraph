using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Indexing;
using CallGraph.Core.Solutions;

namespace CallGraph.Tests;

public sealed class GraphAnalyzerIncludeTestsTests
{
    [Fact]
    public async Task Analyze_WithIncludeTestsFalse_ExcludesTestProjectNodesAndEdges()
    {
        var fixture = CreateFixture();
        var analyzer = new GraphAnalyzer(fixture.Store, new EmptyTargetResolver(), new GraphBuilder());

        var result = await analyzer.AnalyzeAsync(
            new AnalyzeRequest(
                FilePath: fixture.ProductionFilePath,
                Depth: 2,
                Method: "Run",
                SolutionPath: fixture.SolutionPath,
                IncludeTests: false),
            CancellationToken.None);

        Assert.NotNull(result.Graph);
        Assert.Null(result.Error);
        Assert.Single(result.Graph!.Nodes);
        Assert.DoesNotContain(result.Graph.Nodes, node => string.Equals(node.Id, fixture.TestMethodId, StringComparison.Ordinal));
        Assert.Empty(result.Graph.Edges);
    }

    [Fact]
    public async Task Analyze_WithIncludeTestsTrue_KeepsTestProjectNodesAndEdges()
    {
        var fixture = CreateFixture();
        var analyzer = new GraphAnalyzer(fixture.Store, new EmptyTargetResolver(), new GraphBuilder());

        var result = await analyzer.AnalyzeAsync(
            new AnalyzeRequest(
                FilePath: fixture.ProductionFilePath,
                Depth: 2,
                Method: "Run",
                SolutionPath: fixture.SolutionPath,
                IncludeTests: true),
            CancellationToken.None);

        Assert.NotNull(result.Graph);
        Assert.Null(result.Error);
        Assert.Contains(result.Graph!.Nodes, node => string.Equals(node.Id, fixture.TestMethodId, StringComparison.Ordinal));
        Assert.Contains(result.Graph.Edges, edge =>
            string.Equals(edge.From, fixture.ProductionMethodId, StringComparison.Ordinal) &&
            string.Equals(edge.To, fixture.TestMethodId, StringComparison.Ordinal));
    }

    private static Fixture CreateFixture()
    {
        var solutionPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"callgraph-{Guid.NewGuid():N}.sln"));
        var productionFilePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"callgraph-{Guid.NewGuid():N}", "src", "PaymentsService.cs"));
        var testFilePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"callgraph-{Guid.NewGuid():N}", "tests", "PaymentsServiceTests.cs"));

        var productionMethodId = "M:App.PaymentsService.Run()";
        var testMethodId = "M:App.Tests.PaymentsServiceTests.Run()";

        var index = new SolutionIndex
        {
            SolutionId = "sol-1",
            SolutionPath = solutionPath,
            IndexedAtUtc = DateTime.UtcNow,
            SlnOnly = true,
            Nodes =
            [
                new Node
                {
                    Id = productionMethodId,
                    Kind = "method",
                    Display = "App.PaymentsService.Run()",
                    ContainingType = "App.PaymentsService",
                    FilePath = productionFilePath,
                    Accessibility = "public",
                    StartLine = 10
                },
                new Node
                {
                    Id = testMethodId,
                    Kind = "method",
                    Display = "App.Tests.PaymentsServiceTests.Run()",
                    ContainingType = "App.Tests.PaymentsServiceTests",
                    FilePath = testFilePath,
                    Accessibility = "public",
                    StartLine = 12
                }
            ],
            Edges =
            [
                new Edge
                {
                    From = productionMethodId,
                    To = testMethodId,
                    Direction = "outbound",
                    Kind = "calls-direct"
                }
            ],
            ProjectPaths =
            [
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"callgraph-{Guid.NewGuid():N}", "src", "App.csproj")),
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"callgraph-{Guid.NewGuid():N}", "tests", "App.Tests.csproj"))
            ]
        };

        return new Fixture(
            new StubIndexStore(index),
            solutionPath,
            productionFilePath,
            productionMethodId,
            testMethodId);
    }

    private sealed record Fixture(
        StubIndexStore Store,
        string SolutionPath,
        string ProductionFilePath,
        string ProductionMethodId,
        string TestMethodId);

    private sealed class EmptyTargetResolver : ITargetResolver
    {
        public Task<HashSet<string>> ResolveTargetsAsync(
            string solutionPath,
            bool slnOnly,
            string filePath,
            string? methodName,
            CancellationToken cancellationToken)
            => Task.FromResult(new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed class StubIndexStore : IIndexStore
    {
        private readonly SolutionIndex _index;

        public StubIndexStore(SolutionIndex index) => _index = index;

        public Task<SolutionIndex?> LoadAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<SolutionIndex?>(_index);

        public Task<SolutionInfo?> GetSolutionByPathAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<SolutionInfo?>(new SolutionInfo(_index.SolutionId, _index.SolutionPath, _index.SlnOnly));

        public Task ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAsync(SolutionIndex index, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SolutionInfo>> ListSolutionsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DateTime?> GetIndexedAtUtcAsync(string solutionPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SolutionInfo?> GetSolutionByIdAsync(string solutionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexedFileInfo>> ListFilesAsync(string solutionPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListProjectPathsAsync(string solutionPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SolutionInfo>> FindSolutionsByFilePathAsync(string filePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SolutionFileMatch>> FindSolutionsByFilePathSuffixAsync(string relativeFilePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SolutionProjectMatch>> FindProjectsByPathSuffixAsync(string relativeProjectPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SearchFileMatch>> SearchFilesAsync(string pattern, bool useRegex, string? solutionPath, string? solutionId, string? folderPath, string? filePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SearchMethodMatch>> SearchMethodsAsync(string pattern, bool useRegex, string? solutionPath, string? solutionId, string? folderPath, string? filePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SearchMethodMatch>> ListMethodsAsync(string visibility, string? solutionPath, string? solutionId, string? folderPath, string? filePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Node?> GetMethodAsync(string solutionPath, string methodKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Edge>> GetEdgesAsync(string solutionPath, string methodKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateFileAsync(string solutionPath, FileIndex update, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveFileAsync(string solutionPath, string filePath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
