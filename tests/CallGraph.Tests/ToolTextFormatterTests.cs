using CallGraph.Contracts;
using CallGraph.Core.Output;

namespace CallGraph.Tests;

public class ToolTextFormatterTests
{
    [Fact]
    public void FormatAnalyze_ReturnsEmptyString_WhenNoMethodsOrCalls()
    {
        var emptyAnalyze = new AnalyzeToolResponse(Array.Empty<AnalyzeMethodToolRow>(), Array.Empty<AnalyzeCallToolRow>());

        Assert.Equal(string.Empty, ToolTextFormatter.FormatAnalyze(emptyAnalyze));
    }

    [Fact]
    public void FormatAnalyze_ProducesLineBasedMethodAndCallRows()
    {
        var response = new AnalyzeToolResponse(
            new[]
            {
                new AnalyzeMethodToolRow("m1", "Run", "My.Type", "/src/A.cs", 10),
                new AnalyzeMethodToolRow("m2", "Handle", null, null, null)
            },
            new[]
            {
                new AnalyzeCallToolRow("m1", "m2", "outbound")
            });

        var text = ToolTextFormatter.FormatAnalyze(response);

        var expected = "M\tm1\t/src/A.cs:10\tMy.Type\tRun" + Environment.NewLine +
                       "M\tm2\t-\t-\tHandle" + Environment.NewLine +
                       "C\tm1\tm2\toutbound";
        Assert.Equal(expected, text);
    }
}
