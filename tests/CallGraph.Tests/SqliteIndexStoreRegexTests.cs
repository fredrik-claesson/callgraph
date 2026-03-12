using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using CallGraph.Core.Solutions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CallGraph.Tests;

public sealed class SqliteIndexStoreRegexTests
{
    [Fact]
    public async Task SearchFilesAsync_InvalidRegex_DoesNotThrowAndReturnsNoMatches()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        try
        {
            var store = await CreateSeededStoreAsync(dbPath);

            var matches = await store.SearchFilesAsync(
                pattern: "*DeviceType*",
                useRegex: true,
                solutionPath: null,
                solutionId: null,
                folderPath: null,
                filePath: null,
                CancellationToken.None);

            Assert.Empty(matches);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task SearchMethodsAsync_InvalidRegex_DoesNotThrowAndReturnsNoMatches()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        try
        {
            var store = await CreateSeededStoreAsync(dbPath);

            var matches = await store.SearchMethodsAsync(
                pattern: "*DeviceType*",
                useRegex: true,
                solutionPath: null,
                solutionId: null,
                folderPath: null,
                filePath: null,
                CancellationToken.None);

            Assert.Empty(matches);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task SearchFilesAsync_ValidRegex_MatchesExpectedFile()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        try
        {
            var store = await CreateSeededStoreAsync(dbPath);

            var matches = await store.SearchFilesAsync(
                pattern: ".*DeviceType.*",
                useRegex: true,
                solutionPath: null,
                solutionId: null,
                folderPath: null,
                filePath: null,
                CancellationToken.None);

            Assert.Contains(matches, m => m.FilePath.EndsWith("PaymentTerminalDeviceType.cs", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private static async Task<SqliteIndexStore> CreateSeededStoreAsync(string dbPath)
    {
        var store = new SqliteIndexStore(Options.Create(new IndexStoreOptions { DatabasePath = dbPath }));
        var solutionPath = Path.Combine(Path.GetTempPath(), "RegexTestSolution.sln");
        var filePath = Path.Combine(Path.GetTempPath(), "PaymentTerminalDeviceType.cs");

        var index = new SolutionIndex
        {
            SolutionId = "solution-1",
            SolutionPath = solutionPath,
            IndexedAtUtc = DateTime.UtcNow,
            SlnOnly = true,
            Nodes =
            [
                new Node
                {
                    Id = "Asm:Acme.Payment.Terminal.Adapters.DeviceTypeResolver.Resolve()",
                    Kind = "method",
                    Display = "DeviceTypeResolver.Resolve()",
                    ContainingType = "Acme.Payment.Terminal.Adapters.DeviceTypeResolver",
                    FilePath = filePath,
                    StartLine = 1,
                    Accessibility = "public"
                }
            ]
        };

        await store.SaveAsync(index, CancellationToken.None);
        return store;
    }
}
