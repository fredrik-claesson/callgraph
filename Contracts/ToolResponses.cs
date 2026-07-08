namespace CallGraph.Contracts;

public sealed record AnalyzeToolResponse(
    IReadOnlyList<AnalyzeMethodToolRow> Methods,
    IReadOnlyList<AnalyzeCallToolRow> Calls);

public sealed record AnalyzeMethodToolRow(
    string MethodId,
    string MethodName,
    string? ContainingType,
    string? FilePath,
    int? StartLine);

public sealed record AnalyzeCallToolRow(
    string CallerMethodId,
    string CalleeMethodId,
    string Direction);
