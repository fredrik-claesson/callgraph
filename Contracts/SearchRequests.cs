using System.Text.Json.Serialization;

namespace CallGraph.Contracts;

public sealed record SearchFileRequest(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("regex")] bool? Regex = null,
    [property: JsonPropertyName("includeTests")] bool? IncludeTests = null,
    [property: JsonPropertyName("solutionPath")] string? SolutionPath = null,
    [property: JsonPropertyName("solutionId")] string? SolutionId = null,
    [property: JsonPropertyName("folderPath")] string? FolderPath = null,
    [property: JsonPropertyName("filePath")] string? FilePath = null);

public sealed record SearchMethodRequest(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("regex")] bool? Regex = null,
    [property: JsonPropertyName("includeTests")] bool? IncludeTests = null,
    [property: JsonPropertyName("solutionPath")] string? SolutionPath = null,
    [property: JsonPropertyName("solutionId")] string? SolutionId = null,
    [property: JsonPropertyName("folderPath")] string? FolderPath = null,
    [property: JsonPropertyName("filePath")] string? FilePath = null);
