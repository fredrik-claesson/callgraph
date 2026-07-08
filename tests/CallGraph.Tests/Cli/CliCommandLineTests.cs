using CallGraph;
using CallGraph.Cli;
using Xunit;

namespace CallGraph.Tests.Cli;

public sealed class CliCommandLineTests
{
    [Fact]
    public void Parse_Query_CapturesPositionalSql()
    {
        Assert.True(CliCommandLine.TryParse(new[] { "query", "SELECT * FROM Methods" }, out var opts, out var err));
        Assert.Null(err);
        Assert.NotNull(opts.ToolCommand);
        Assert.Equal("query", opts.ToolCommand!.Name);
        Assert.Equal("SELECT * FROM Methods", opts.ToolCommand.Options["sql"]);
    }

    [Fact]
    public void Parse_UnknownWatchFlag_IsRejected()
    {
        Assert.False(CliCommandLine.TryParse(new[] { "--watch" }, out _, out var err));
        Assert.NotNull(err);
    }
}
