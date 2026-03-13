using CallGraph.Core.Extraction;

namespace CallGraph.Tests;

public sealed class MethodSourceExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ReturnsSingleMethodByContainingTypeAndName()
    {
        var filePath = WriteTempFile(
            """
            namespace Demo;

            public sealed class A
            {
                public int Sum(int x, int y)
                {
                    return x + y;
                }
            }
            """);

        try
        {
            var extractor = new MethodSourceExtractor();
            var result = await extractor.ExtractAsync(
                new MethodSourceExtractionRequest(
                    FilePath: filePath,
                    MethodName: "Sum",
                    ContainingType: "Demo.A",
                    Signature: null,
                    StartLine: null,
                    Mode: "signature_plus_body"),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Match);
            Assert.Equal("Sum", result.Match!.MethodName);
            Assert.Equal("Demo.A", result.Match.ContainingType);
            Assert.Contains("public int Sum(int x, int y)", result.Match.Content, StringComparison.Ordinal);
            Assert.Contains("return x + y;", result.Match.Content, StringComparison.Ordinal);
            Assert.True(result.Match.StartLine > 0);
            Assert.True(result.Match.EndLine >= result.Match.StartLine);
            Assert.True(result.Match.EndByte > result.Match.StartByte);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_BodyWithoutComments_RemovesLineAndBlockComments()
    {
        var filePath = WriteTempFile(
            """
            namespace Demo;

            public sealed class A
            {
                public int Sum(int x, int y)
                {
                    // line comment
                    /* block comment */
                    return x + y;
                }
            }
            """);

        try
        {
            var extractor = new MethodSourceExtractor();
            var result = await extractor.ExtractAsync(
                new MethodSourceExtractionRequest(
                    FilePath: filePath,
                    MethodName: "Sum",
                    ContainingType: "Demo.A",
                    Signature: null,
                    StartLine: null,
                    Mode: "body_without_comments"),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Match);
            Assert.DoesNotContain("line comment", result.Match!.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("block comment", result.Match.Content, StringComparison.Ordinal);
            Assert.Contains("return x + y;", result.Match.Content, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_AmbiguousName_ReturnsCandidates()
    {
        var filePath = WriteTempFile(
            """
            namespace Demo;

            public sealed class A
            {
                public int Sum(int x, int y) => x + y;
                public int Sum(int x, int y, int z) => x + y + z;
            }
            """);

        try
        {
            var extractor = new MethodSourceExtractor();
            var result = await extractor.ExtractAsync(
                new MethodSourceExtractionRequest(
                    FilePath: filePath,
                    MethodName: "Sum",
                    ContainingType: "Demo.A",
                    Signature: null,
                    StartLine: null,
                    Mode: "body_only"),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Contains("ambiguous", result.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(result.Candidates);
            Assert.Equal(2, result.Candidates!.Count);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static string WriteTempFile(string contents)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"callgraph-method-source-{Guid.NewGuid():N}.cs");
        File.WriteAllText(filePath, contents);
        return filePath;
    }
}
