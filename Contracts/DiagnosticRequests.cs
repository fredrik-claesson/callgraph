namespace CallGraph.Contracts;

/// <summary>
/// Request to list unused code diagnostics (classes, methods, parameters, properties, fields, etc.).
/// </summary>
/// <param name="ProjectPath">Absolute path to .csproj file (required).</param>
/// <param name="FolderPath">Optional folder path to scope search (mutually exclusive with FilePath).</param>
/// <param name="FilePath">Optional file path to scope search (mutually exclusive with FolderPath).</param>
public sealed record UnusedDiagnosticsRequest(
    string ProjectPath,
    string? FolderPath = null,
    string? FilePath = null);

/// <summary>
/// Request to list warning diagnostics.
/// </summary>
/// <param name="ProjectPath">Absolute path to .csproj file (required).</param>
/// <param name="FolderPath">Optional folder path to scope search (mutually exclusive with FilePath).</param>
/// <param name="FilePath">Optional file path to scope search (mutually exclusive with FolderPath).</param>
public sealed record WarningDiagnosticsRequest(
    string ProjectPath,
    string? FolderPath = null,
    string? FilePath = null);
