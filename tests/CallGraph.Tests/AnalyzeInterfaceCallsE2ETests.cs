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

    [Fact]
    public async Task Analyze_BridgesPublisherPayloadToHandlerAndInterfaceImplementation()
    {
        var solutionPath = GetSolutionPath();
        var creatorPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "InterfaceCallE2E", "Services", "AdyenTerminalOrderCreator.cs");
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
                    FilePath: creatorPath,
                    Depth: 3,
                    Method: "CreateAdyenTerminalOrderAsync",
                    SolutionPath: solutionPath,
                    SolutionId: null,
                    Direction: "outbound",
                    Visibility: "internal"),
                CancellationToken.None);

            Assert.True(result.Graph is not null, $"Analyze failed: {result.Error?.Kind} - {result.Error?.Detail}");
            var graph = result.Graph!;

            var creator = FindNodeIdByMethodName(graph, "CreateAdyenTerminalOrderAsync");
            var handler = FindNodeIdByMethodName(graph, "HandleInternalAsync");
            var terminalComponent = FindNodeIdByMethodName(graph, "CreateTerminalProductSubscriptionOnTerminalOrder");

            Assert.True(creator is not null, "Missing creator method");
            Assert.True(handler is not null, "Missing handler method");
            Assert.True(terminalComponent is not null, "Missing terminal component implementation method");
            Assert.Contains(graph.Edges, edge => edge.From == creator && edge.To == handler);
            Assert.Contains(graph.Edges, edge => edge.From == handler && edge.To == terminalComponent);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task IncrementalReindex_PreservesInboundCallersToUpdatedFileMethods()
    {
        var sourceSolutionPath = GetSolutionPath();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"callgraph-e2e-{Guid.NewGuid():N}");
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        try
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();

            CopyDirectory(Path.GetDirectoryName(sourceSolutionPath)!, tempRoot);
            var solutionPath = Path.Combine(tempRoot, "InterfaceCallE2E.sln");
            var helperPath = Path.Combine(tempRoot, "InterfaceCallE2E", "Services", "Helper.cs");

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

            var before = await analyzer.AnalyzeAsync(
                new AnalyzeRequest(
                    FilePath: helperPath,
                    Depth: 1,
                    Method: "Help",
                    SolutionPath: solutionPath,
                    SolutionId: null,
                    Direction: "inbound",
                    Visibility: "internal"),
                CancellationToken.None);

            Assert.True(before.Graph is not null, $"Analyze failed before reindex: {before.Error?.Kind} - {before.Error?.Detail}");
            var beforeGraph = before.Graph!;
            var beforeHelper = FindNodeId(beforeGraph, "Helper.Help()", "InterfaceCallE2E.Infrastructure.Services.Helper.Help()");
            var beforeCaller = FindNodeId(beforeGraph, "Worker.DirectHelper()", "InterfaceCallE2E.Application.Services.Worker.DirectHelper()");
            Assert.True(beforeHelper is not null, "Missing Helper.Help() before reindex");
            Assert.True(beforeCaller is not null, "Missing Worker.DirectHelper() before reindex");
            Assert.Contains(beforeGraph.Edges, edge => edge.From == beforeCaller && edge.To == beforeHelper);

            File.AppendAllText(helperPath, Environment.NewLine + " ");
            File.SetLastWriteTimeUtc(helperPath, DateTime.UtcNow.AddSeconds(5));

            await pipeline.RunAsync(
                new IndexJobRequest("job-2", solutionId, solutionPath, false, true),
                CancellationToken.None);

            var after = await analyzer.AnalyzeAsync(
                new AnalyzeRequest(
                    FilePath: helperPath,
                    Depth: 1,
                    Method: "Help",
                    SolutionPath: solutionPath,
                    SolutionId: null,
                    Direction: "inbound",
                    Visibility: "internal"),
                CancellationToken.None);

            Assert.True(after.Graph is not null, $"Analyze failed after reindex: {after.Error?.Kind} - {after.Error?.Detail}");
            var afterGraph = after.Graph!;
            var afterHelper = FindNodeId(afterGraph, "Helper.Help()", "InterfaceCallE2E.Infrastructure.Services.Helper.Help()");
            var afterCaller = FindNodeId(afterGraph, "Worker.DirectHelper()", "InterfaceCallE2E.Application.Services.Worker.DirectHelper()");
            Assert.True(afterHelper is not null, "Missing Helper.Help() after reindex");
            Assert.True(afterCaller is not null, "Missing Worker.DirectHelper() after reindex");
            Assert.Contains(afterGraph.Edges, edge => edge.From == afterCaller && edge.To == afterHelper);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Analyze_ConditionalInterfaceCall_MapsToImplementations()
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
                    Depth: 2,
                    Method: "RunWithConditional",
                    SolutionPath: solutionPath,
                    SolutionId: null,
                    Direction: "outbound",
                    Visibility: "internal"),
                CancellationToken.None);

            Assert.True(result.Graph is not null, $"Analyze failed: {result.Error?.Kind} - {result.Error?.Detail}");
            var graph = result.Graph!;

            var conditionalCaller = FindNodeIdByMethodName(graph, "RunWithConditional");
            var emailNotify = FindNodeIdByMethodName(graph, "EmailNotifier.Notify");
            var smsNotify = FindNodeIdByMethodName(graph, "SmsNotifier.Notify");

            Assert.True(conditionalCaller is not null, "Missing Worker.RunWithConditional");
            Assert.True(emailNotify is not null, "Missing EmailNotifier.Notify");
            Assert.True(smsNotify is not null, "Missing SmsNotifier.Notify");
            Assert.Contains(graph.Edges, edge => edge.From == conditionalCaller && edge.To == emailNotify);
            Assert.Contains(graph.Edges, edge => edge.From == conditionalCaller && edge.To == smsNotify);
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

    private static string? FindNodeIdByMethodName(Graph graph, string methodName)
    {
        var match = graph.Nodes.FirstOrDefault(n =>
            (!string.IsNullOrWhiteSpace(n.Display) && n.Display.Contains(methodName, StringComparison.Ordinal))
            || n.Id.Contains($".{methodName}(", StringComparison.Ordinal));
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

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var filePath in Directory.GetFiles(sourceDirectory))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(filePath));
            File.Copy(filePath, destinationPath, overwrite: true);
        }

        foreach (var directoryPath in Directory.GetDirectories(sourceDirectory))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(directoryPath));
            CopyDirectory(directoryPath, destinationPath);
        }
    }

    private sealed class AllowAllProjectFilter : IProjectFilter
    {
        public bool IsTestProject(Microsoft.CodeAnalysis.Project project) => false;
    }
}
