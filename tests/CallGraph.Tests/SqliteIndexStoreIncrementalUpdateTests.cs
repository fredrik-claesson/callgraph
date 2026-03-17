using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using CallGraph.Core.Solutions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CallGraph.Tests;

public sealed class SqliteIndexStoreIncrementalUpdateTests
{
    [Fact]
    public async Task UpdateFileAsync_MethodKeyMovesToDifferentFile_RehomesMethodWithoutDuplicateKeyFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"callgraph-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "index.db");
        var solutionPath = Path.Combine(tempDir, "MoveTest.sln");
        var oldFilePath = Path.Combine(tempDir, "OldLoader.cs");
        var newFilePath = Path.Combine(tempDir, "NewLoader.cs");
        var callerFilePath = Path.Combine(tempDir, "Caller.cs");
        var oldDependencyFilePath = Path.Combine(tempDir, "Before.cs");
        var newDependencyFilePath = Path.Combine(tempDir, "After.cs");

        const string movedMethodKey = "Asm:Demo.Loader.Run()";
        const string callerKey = "Asm:Demo.Caller.Invoke()";
        const string oldDependencyKey = "Asm:Demo.Dependency.Before()";
        const string newDependencyKey = "Asm:Demo.Dependency.After()";

        try
        {
            var store = CreateStore(dbPath);

            var seedIndex = new SolutionIndex
            {
                SolutionId = "solution-1",
                SolutionPath = solutionPath,
                IndexedAtUtc = DateTime.UtcNow,
                SlnOnly = true,
                Nodes =
                [
                    CreateNode(movedMethodKey, oldFilePath, "Demo.Loader", "Loader.Run()"),
                    CreateNode(callerKey, callerFilePath, "Demo.Caller", "Caller.Invoke()"),
                    CreateNode(oldDependencyKey, oldDependencyFilePath, "Demo.Dependency", "Dependency.Before()")
                ],
                Edges =
                [
                    CreateEdge(callerKey, movedMethodKey),
                    CreateEdge(movedMethodKey, oldDependencyKey)
                ]
            };

            await store.SaveAsync(seedIndex, CancellationToken.None);

            await store.UpdateFileAsync(
                solutionPath,
                new FileIndex
                {
                    FilePath = newFilePath,
                    Nodes =
                    [
                        CreateNode(movedMethodKey, newFilePath, "Demo.Loader", "Loader.Run()"),
                        CreateNode(newDependencyKey, newDependencyFilePath, "Demo.Dependency", "Dependency.After()")
                    ],
                    Edges =
                    [
                        CreateEdge(movedMethodKey, newDependencyKey)
                    ]
                },
                CancellationToken.None);

            var loaded = await store.LoadAsync(solutionPath, CancellationToken.None);

            Assert.NotNull(loaded);

            var movedNode = Assert.Single(loaded!.Nodes, node => node.Id == movedMethodKey);
            Assert.Equal(newFilePath, movedNode.FilePath);
            Assert.Contains(loaded.Edges, edge => edge.From == callerKey && edge.To == movedMethodKey);
            Assert.Contains(loaded.Edges, edge => edge.From == movedMethodKey && edge.To == newDependencyKey);
            Assert.DoesNotContain(loaded.Edges, edge => edge.From == movedMethodKey && edge.To == oldDependencyKey);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static SqliteIndexStore CreateStore(string dbPath)
        => new(Options.Create(new IndexStoreOptions { DatabasePath = dbPath }));

    private static Node CreateNode(string id, string filePath, string containingType, string display)
        => new()
        {
            Id = id,
            FilePath = filePath,
            Kind = "method",
            Display = display,
            ContainingType = containingType,
            StartLine = 1,
            Accessibility = "public"
        };

    private static Edge CreateEdge(string from, string to)
        => new()
        {
            From = from,
            To = to,
            Direction = "outbound",
            Kind = "calls-direct"
        };
}
