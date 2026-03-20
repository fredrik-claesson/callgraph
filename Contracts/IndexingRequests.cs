using System.Text.Json.Serialization;

namespace CallGraph.Contracts;

public sealed record IndexRequest(
    [property: JsonPropertyName("solutionPath")] string SolutionPath,
    [property: JsonPropertyName("slnOnly")] bool SlnOnly = true);

public sealed record ReindexRequest(
    [property: JsonPropertyName("solutionPath")] string SolutionPath,
    [property: JsonPropertyName("slnOnly")] bool SlnOnly = true);

public sealed record AnalyzeRequest(
    [property: JsonPropertyName("filepath")] string FilePath,
    [property: JsonPropertyName("depth")] int? Depth,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("solutionPath")] string? SolutionPath = null,
    [property: JsonPropertyName("solutionId")] string? SolutionId = null,
    [property: JsonPropertyName("direction")] string? Direction = null,
    [property: JsonPropertyName("visibility")] string? Visibility = null,
    [property: JsonPropertyName("includeTests")] bool? IncludeTests = null);
