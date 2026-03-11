namespace CallGraph.Contracts;

/// <summary>
/// Represents a diagnostic (unused code or warning) found in source code.
/// </summary>
public sealed record Diagnostic(
    string Id,
    string Severity,
    string Message,
    string FilePath,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn);

/// <summary>
/// Response containing unused code diagnostics with overflow tracking.
/// </summary>
public sealed record UnusedDiagnosticsResponse(
    IReadOnlyList<Diagnostic> Diagnostics,
    int TotalCount,
    bool Truncated);

/// <summary>
/// Response containing warning diagnostics with overflow tracking.
/// </summary>
public sealed record WarningDiagnosticsResponse(
    IReadOnlyList<Diagnostic> Diagnostics,
    int TotalCount,
    bool Truncated);
