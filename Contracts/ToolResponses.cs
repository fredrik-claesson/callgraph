namespace CallGraph.Contracts;

public sealed record SearchFileToolResponse(int Count, IReadOnlyList<SearchFileToolRow> Matches);

public sealed record SearchFileToolRow(string FilePath);

public sealed record SearchMethodToolResponse(int Count, IReadOnlyList<SearchMethodToolRow> Matches);

public sealed record SearchMethodToolRow(
    string MethodName,
    string? Signature,
    string? FilePath,
    int? StartLine,
    string? ContainingType);

public sealed record DiagnosticToolResponse(
    int TotalCount,
    IReadOnlyList<DiagnosticToolRow> Diagnostics);

public sealed record DiagnosticToolRow(
    string Id,
    string Severity,
    string Message,
    string FilePath,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn);

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
