using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Indexing;
using CallGraph.Core.Projects;
using CallGraph.Core.Solutions;
using Microsoft.Data.Sqlite;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CallGraph.Tests;

public sealed class AnalyzeInterfaceCallsE2ETests
{
    [Fact]
    public async Task AnalyzesInterfaceCallsWithMixedVisibility()
    {
        var solutionPath = GetSolutionPath();
        var workerPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "InterfaceCallE2E", "Services", "Worker.cs");
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        try
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();

            var indexStore = new SqliteIndexStore(Options.Create(new IndexStoreOptions { DatabasePath = dbPath }));
            var solutionLoader = new SolutionLoader(new AllowAllProjectFilter(), new SolutionFileParser());
            var graphBuilder = new GraphBuilder();
            var pipeline = new IndexingPipeline(
                solutionLoader,
                new ProjectIndexer(),
                new FileIndexer(solutionLoader),
                graphBuilder,
                indexStore,
                NullLogger<IndexingPipeline>.Instance);
            var solutionId = SolutionIdentity.FromPath(solutionPath);

            await pipeline.RunAsync(
                new IndexJobRequest("job-1", solutionId, solutionPath, false, false),
                CancellationToken.None);

            var analyzer = new GraphAnalyzer(indexStore, new TargetResolver(solutionLoader), graphBuilder);

            var result = await analyzer.AnalyzeAsync(
                new AnalyzeRequest(
                    FilePath: workerPath,
                    Depth: 3,
                    Method: "Run",
                    SolutionPath: solutionPath,
                    SolutionId: null,
                    Direction: "outbound",
                    Visibility: "internal"),
                CancellationToken.None);

            Assert.True(result.Graph is not null, $"Analyze failed: {result.Error?.Kind} - {result.Error?.Detail}");
            var graph = result.Graph!;

            var workerRun = FindNodeId(graph, "Worker.Run()", "InterfaceCallE2E.Application.Services.Worker.Run()");
            var directHelper = FindNodeId(graph, "Worker.DirectHelper()", "InterfaceCallE2E.Application.Services.Worker.DirectHelper()");
            var helperHelp = FindNodeId(graph, "Helper.Help()", "InterfaceCallE2E.Infrastructure.Services.Helper.Help()");
            var emailNotify = FindNodeId(graph, "EmailNotifier.Notify(string)", "InterfaceCallE2E.Infrastructure.Notifications.EmailNotifier.Notify(string)");
            var smsNotify = FindNodeId(graph, "SmsNotifier.Notify(string)", "InterfaceCallE2E.Infrastructure.Notifications.SmsNotifier.Notify(string)");
            var privateUtility = FindNodeId(graph, "Worker.PrivateUtility()", "InterfaceCallE2E.Application.Services.Worker.PrivateUtility()");
            var utilityDoWork = FindNodeId(graph, "Utility.DoWork()", "InterfaceCallE2E.Application.Services.Utility.DoWork()");

            Assert.True(workerRun is not null, "Missing Worker.Run()");
            Assert.True(directHelper is not null, "Missing Worker.DirectHelper()");
            Assert.True(helperHelp is not null, "Missing Helper.Help()");
            Assert.True(emailNotify is not null, "Missing EmailNotifier.Notify(string)");
            Assert.True(smsNotify is not null, "Missing SmsNotifier.Notify(string)");
            Assert.True(privateUtility is not null, "Missing Worker.PrivateUtility()");
            Assert.True(utilityDoWork is not null, "Missing Utility.DoWork()");

            Assert.Contains(graph.Edges, e => e.From == directHelper && e.To == helperHelp);
            Assert.Contains(graph.Edges, e => e.From == workerRun && e.To == emailNotify);
            Assert.Contains(graph.Edges, e => e.From == workerRun && e.To == smsNotify);
            Assert.Contains(graph.Edges, e => e.From == directHelper && e.To == privateUtility);
            Assert.Contains(graph.Edges, e => e.From == workerRun && e.To == utilityDoWork);

            Assert.Contains(graph.Nodes, n => n.Accessibility == "public");
            Assert.Contains(graph.Nodes, n => n.Accessibility == "protected");
            Assert.Contains(graph.Nodes, n => n.Accessibility == "internal");
            Assert.Contains(graph.Nodes, n => n.Accessibility == "private");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExternalVisibility_TraversesPrivateMethods_CountsClassBasedDepth()
    {
        var solutionPath = GetSolutionPath();
        var workerPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "InterfaceCallE2E", "Services", "Worker.cs");
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        try
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();

            var indexStore = new SqliteIndexStore(Options.Create(new IndexStoreOptions { DatabasePath = dbPath }));
            var solutionLoader = new SolutionLoader(new AllowAllProjectFilter(), new SolutionFileParser());
            var graphBuilder = new GraphBuilder();
            var pipeline = new IndexingPipeline(
                solutionLoader,
                new ProjectIndexer(),
                new FileIndexer(solutionLoader),
                graphBuilder,
                indexStore,
                NullLogger<IndexingPipeline>.Instance);
            var solutionId = SolutionIdentity.FromPath(solutionPath);

            await pipeline.RunAsync(
                new IndexJobRequest("job-1", solutionId, solutionPath, false, false),
                CancellationToken.None);

            var analyzer = new GraphAnalyzer(indexStore, new TargetResolver(solutionLoader), graphBuilder);

            // External visibility with depth=1 should:
            // - Traverse private methods within Worker class (depth stays at 0 for same class)
            // - Reach other classes at depth 1 (Helper, EmailNotifier, SmsNotifier, Utility)
            var result = await analyzer.AnalyzeAsync(
                new AnalyzeRequest(
                    FilePath: workerPath,
                    Depth: 1,
                    Method: "Run",
                    SolutionPath: solutionPath,
                    SolutionId: null,
                    Direction: "outbound",
                    Visibility: "external"),
                CancellationToken.None);

            Assert.True(result.Graph is not null, $"Analyze failed: {result.Error?.Kind} - {result.Error?.Detail}");
            var graph = result.Graph!;

            // Should find Worker.Run() (the target)
            var workerRun = FindNodeId(graph, "Worker.Run()", "InterfaceCallE2E.Application.Services.Worker.Run()");
            Assert.True(workerRun is not null, "Missing Worker.Run()");

            // Should find Worker.DirectHelper() - private method in same class, traversed at depth 0
            var directHelper = FindNodeId(graph, "Worker.DirectHelper()", "InterfaceCallE2E.Application.Services.Worker.DirectHelper()");
            Assert.True(directHelper is not null, "Missing Worker.DirectHelper() - external visibility should traverse private methods");

            // Should find Worker.PrivateUtility() - called by DirectHelper, still same class = depth 0
            var privateUtility = FindNodeId(graph, "Worker.PrivateUtility()", "InterfaceCallE2E.Application.Services.Worker.PrivateUtility()");
            Assert.True(privateUtility is not null, "Missing Worker.PrivateUtility() - external visibility should traverse all same-class calls");

            // Should find Helper.Help() - different class, depth 1
            var helperHelp = FindNodeId(graph, "Helper.Help()", "InterfaceCallE2E.Infrastructure.Services.Helper.Help()");
            Assert.True(helperHelp is not null, "Missing Helper.Help() - should be reached at depth 1");

            // Should find interface implementations at depth 1
            var emailNotify = FindNodeId(graph, "EmailNotifier.Notify(string)", "InterfaceCallE2E.Infrastructure.Notifications.EmailNotifier.Notify(string)");
            var smsNotify = FindNodeId(graph, "SmsNotifier.Notify(string)", "InterfaceCallE2E.Infrastructure.Notifications.SmsNotifier.Notify(string)");
            Assert.True(emailNotify is not null, "Missing EmailNotifier.Notify(string)");
            Assert.True(smsNotify is not null, "Missing SmsNotifier.Notify(string)");

            // Verify edges exist through private methods
            Assert.Contains(graph.Edges, e => e.From == workerRun && e.To == directHelper);
            Assert.Contains(graph.Edges, e => e.From == directHelper && e.To == privateUtility);
            Assert.Contains(graph.Edges, e => e.From == directHelper && e.To == helperHelp);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ListMethods_RespectsVisibilityFilter()
    {
        var solutionPath = GetSolutionPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        try
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();

            var indexStore = new SqliteIndexStore(Options.Create(new IndexStoreOptions { DatabasePath = dbPath }));
            var solutionLoader = new SolutionLoader(new AllowAllProjectFilter(), new SolutionFileParser());
            var graphBuilder = new GraphBuilder();
            var pipeline = new IndexingPipeline(
                solutionLoader,
                new ProjectIndexer(),
                new FileIndexer(solutionLoader),
                graphBuilder,
                indexStore,
                NullLogger<IndexingPipeline>.Instance);
            var solutionId = SolutionIdentity.FromPath(solutionPath);

            await pipeline.RunAsync(
                new IndexJobRequest("job-1", solutionId, solutionPath, false, false),
                CancellationToken.None);

            var externalMethods = await indexStore.ListMethodsAsync(
                "external",
                solutionPath,
                solutionId: null,
                folderPath: null,
                filePath: null,
                CancellationToken.None);
            var internalMethods = await indexStore.ListMethodsAsync(
                "internal",
                solutionPath,
                solutionId: null,
                folderPath: null,
                filePath: null,
                CancellationToken.None);

            Assert.NotEmpty(externalMethods);
            Assert.NotEmpty(internalMethods);
            Assert.True(internalMethods.Count > externalMethods.Count);

            Assert.All(externalMethods, m =>
                Assert.True(IsExternalAccessibility(m.Method.Accessibility),
                    $"Unexpected external accessibility: {m.Method.Accessibility ?? "<null>"} for {m.Method.Id}"));

            Assert.Contains(internalMethods, m => string.Equals(m.Method.Accessibility, "private", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(internalMethods, m => string.Equals(m.Method.Accessibility, "internal", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private static string? FindNodeId(Graph graph, string displaySuffix, string idSuffix)
    {
        var match = graph.Nodes.FirstOrDefault(n => n.Id.EndsWith(idSuffix, StringComparison.Ordinal));
        if (match is not null)
            return match.Id;

        match = graph.Nodes.FirstOrDefault(n => (n.Display ?? "").EndsWith(displaySuffix, StringComparison.Ordinal));
        return match?.Id;
    }

    private static string GetSolutionPath()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(root, "tests", "CallGraph.Tests", "TestAssets", "InterfaceCallE2E", "InterfaceCallE2E.sln");
    }

    private static bool IsExternalAccessibility(string? accessibility)
    {
        if (string.IsNullOrWhiteSpace(accessibility))
            return false;

        return accessibility.Equals("public", StringComparison.OrdinalIgnoreCase)
               || accessibility.Equals("protected", StringComparison.OrdinalIgnoreCase)
               || accessibility.Equals("protected internal", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AllowAllProjectFilter : IProjectFilter
    {
        public bool IsTestProject(Microsoft.CodeAnalysis.Project project) => false;
    }
}
