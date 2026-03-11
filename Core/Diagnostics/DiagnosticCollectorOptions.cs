namespace CallGraph.Core.Diagnostics;

public sealed class DiagnosticCollectorOptions
{
    /// <summary>
    /// Optional: absolute paths to analyzer assemblies (or directories containing analyzer assemblies)
    /// that should always be used when collecting diagnostics.
    ///
    /// Intended for internal deployments where you want a stable set of analyzers that won't be
    /// overwritten by builds of the analyzed solution (avoids file lock issues).
    /// </summary>
    public string[] BundledAnalyzerPaths { get; set; } = [];
}
