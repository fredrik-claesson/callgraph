using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Diagnostics;

/// <summary>
/// Collects diagnostics (unused code, warnings) from Roslyn compilations.
/// </summary>
public interface IDiagnosticCollector
{
    /// <summary>
    /// Collects unused code diagnostics (IDE0051, IDE0052, IDE0060, CA1812) from projects.
    /// </summary>
    Task<IReadOnlyList<Contracts.Diagnostic>> CollectUnusedDiagnosticsAsync(
        IReadOnlyList<Project> projects,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Collects warning diagnostics (DiagnosticSeverity.Warning) from projects.
    /// </summary>
    Task<IReadOnlyList<Contracts.Diagnostic>> CollectWarningDiagnosticsAsync(
        IReadOnlyList<Project> projects,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken);
}
