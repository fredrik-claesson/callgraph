using CallGraph.Cli;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CallGraph.Tests.Cli;

public sealed class ToolCommandExecutorTests
{
    [Fact]
    public async Task Execute_UnknownCommand_ReturnsError()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(
            new ToolCommand("search-file", new Dictionary<string, string?>()),
            CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode); // removed command no longer supported
    }

    [Fact]
    public async Task Execute_QueryCommand_DispatchesToQueryCommandExecutor()
    {
        var dbPath = SeedDb();
        var executor = CreateExecutor(dbPath);

        var result = await executor.ExecuteAsync(
            new ToolCommand("query", new Dictionary<string, string?> { ["sql"] = "SELECT Display FROM Methods" }),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Display\nFooService.Bar(int)", result.Stdout!.TrimEnd('\n'));
    }

    private static string SeedDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cg-tool-executor-{Guid.NewGuid():N}.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE Methods(Display TEXT); INSERT INTO Methods VALUES('FooService.Bar(int)');";
        cmd.ExecuteNonQuery();
        return path;
    }

    private static ToolCommandExecutor CreateExecutor(string? databasePath = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGraphAnalyzer>(new NullGraphAnalyzer());
        services.AddSingleton(Options.Create(new IndexStoreOptions { DatabasePath = databasePath }));
        var provider = services.BuildServiceProvider();
        return new ToolCommandExecutor(provider, null!);
    }

    private sealed class NullGraphAnalyzer : IGraphAnalyzer
    {
        public Task<AnalyzeResult> AnalyzeAsync(AnalyzeRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
