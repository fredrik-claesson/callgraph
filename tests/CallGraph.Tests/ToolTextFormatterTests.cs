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

        Assert.Equal(string.Empty, ToolTextFormatter.FormatSearchFiles(emptyFiles));
        Assert.Equal(string.Empty, ToolTextFormatter.FormatSearchMethods(emptyMethods));
    }
}
