using CallGraph.Cli;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CallGraph.Tests.Cli;

public sealed class QueryCommandExecutorTests
{
    private static string SeedDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cg-query-{Guid.NewGuid():N}.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE Methods(Display TEXT, StartLine INTEGER); " +
                          "INSERT INTO Methods VALUES('FooService.Bar(int)', 42);";
        cmd.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsTabSeparatedRowsWithHeader()
    {
        var path = SeedDb();
        var result = await QueryCommandExecutor.ExecuteAsync(
            "SELECT Display, StartLine FROM Methods", path, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Display\tStartLine\nFooService.Bar(int)\t42", result.Stdout!.TrimEnd('\n'));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_OnWriteAttempt()
    {
        var path = SeedDb();
        var result = await QueryCommandExecutor.ExecuteAsync(
            "DELETE FROM Methods", path, CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
    }
}
