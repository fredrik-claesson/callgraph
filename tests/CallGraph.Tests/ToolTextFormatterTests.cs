using CallGraph.Contracts;
using CallGraph.Core.Output;

namespace CallGraph.Tests;

public class ToolTextFormatterTests
{
    [Fact]
    public void FormatSearchFiles_ProducesOnePathPerLine()
    {
        var response = new SearchFileToolResponse(
            2,
            new[]
            {
                new SearchFileToolRow("/src/A.cs"),
                new SearchFileToolRow("/src/B.cs")
            });

        var text = ToolTextFormatter.FormatSearchFiles(response);

        Assert.Equal("/src/A.cs" + Environment.NewLine + "/src/B.cs", text);
    }

    [Fact]
    public void FormatSearchMethods_ProducesTabSeparatedLines()
    {
        var response = new SearchMethodToolResponse(
            2,
            new[]
            {
                new SearchMethodToolRow("Handle", "My.Type.Handle(string)", "/src/Foo.cs", 42, "My.Type"),
                new SearchMethodToolRow("Run", null, null, null, null)
            });

        var text = ToolTextFormatter.FormatSearchMethods(response);

        var expected = "/src/Foo.cs:42\tMy.Type\tHandle\tMy.Type.Handle(string)" + Environment.NewLine +
                       "-\t-\tRun\t-";
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Formatters_ReturnEmptyString_WhenNoMatches()
    {
        var emptyFiles = new SearchFileToolResponse(0, Array.Empty<SearchFileToolRow>());
        var emptyMethods = new SearchMethodToolResponse(0, Array.Empty<SearchMethodToolRow>());
        var emptyAnalyze = new AnalyzeToolResponse(Array.Empty<AnalyzeMethodToolRow>(), Array.Empty<AnalyzeCallToolRow>());

        Assert.Equal(string.Empty, ToolTextFormatter.FormatSearchFiles(emptyFiles));
        Assert.Equal(string.Empty, ToolTextFormatter.FormatSearchMethods(emptyMethods));
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
