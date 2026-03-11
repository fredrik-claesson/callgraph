using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallGraph.Core.Diagnostics;

/// <summary>
/// Collects diagnostics from Roslyn compilations.
/// </summary>
public sealed class DiagnosticCollector : IDiagnosticCollector
{
    private static readonly HashSet<string> UnusedDiagnosticIds = new(StringComparer.Ordinal)
    {
        // IDE analyzers (loaded from Microsoft.CodeAnalysis.CSharp.Features)
        "IDE0051", // Remove unused private members
        "IDE0052", // Remove unread private members
        "IDE0060", // Remove unused parameter
        // Compiler warnings (always available)
        "CS0169",  // Field is never used
        "CS0414",  // Field is assigned but its value is never used
        "CS0168",  // Variable is declared but never used
        "CS0219",  // Variable is assigned but its value is never used
        "CS8321",  // Local function is declared but never used
    };

    private static readonly Lazy<ImmutableArray<DiagnosticAnalyzer>> BuiltInAnalyzers = new(LoadBuiltInAnalyzers);

    private readonly ILogger<DiagnosticCollector> _logger;
    private readonly DiagnosticCollectorOptions _options;
    private readonly Lazy<ImmutableArray<DiagnosticAnalyzer>> _bundledAnalyzers;

    public DiagnosticCollector(
        ILogger<DiagnosticCollector> logger,
        IOptions<DiagnosticCollectorOptions>? options = null)
    {
        _logger = logger;
        _options = options?.Value ?? new DiagnosticCollectorOptions();
        _bundledAnalyzers = new Lazy<ImmutableArray<DiagnosticAnalyzer>>(
            () => LoadBundledAnalyzers(_options.BundledAnalyzerPaths, _logger));
    }

    private ImmutableArray<DiagnosticAnalyzer> GetAnalyzersToUse(Project project, string language)
    {
        var fromProject = GetProjectAnalyzers(project, language);
        var fromBundled = _bundledAnalyzers.Value;

        if (fromProject.IsEmpty)
            return fromBundled;

        if (fromBundled.IsEmpty)
            return fromProject;

        return fromProject
            .AddRange(fromBundled)
            .GroupBy(a => a.GetType().FullName, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToImmutableArray();
    }

    private static ImmutableArray<DiagnosticAnalyzer> GetProjectAnalyzers(Project project, string language)
    {
        if (project.AnalyzerReferences.Count == 0)
            return ImmutableArray<DiagnosticAnalyzer>.Empty;

        // IMPORTANT: Loading analyzers from a project's build output (bin/obj) can lock those DLLs
        // in our long-running MCP process, which then breaks subsequent builds of the target repo.
        // We therefore skip analyzer references that appear to come from build output folders.
        return project.AnalyzerReferences
            .SelectMany(r =>
            {
                if (r is AnalyzerFileReference fileRef && IsLikelyBuildOutputPath(fileRef.FullPath))
                    return ImmutableArray<DiagnosticAnalyzer>.Empty;

                return r.GetAnalyzers(language);
            })
            .ToImmutableArray();
    }

    private static bool IsLikelyBuildOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = path.Replace('/', '\\');
        return normalized.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase);
    }

    private static ImmutableArray<DiagnosticAnalyzer> LoadBundledAnalyzers(
        IReadOnlyList<string> bundledAnalyzerPaths,
        ILogger logger)
    {
        if (bundledAnalyzerPaths.Count == 0)
            return ImmutableArray<DiagnosticAnalyzer>.Empty;

        var analyzers = new List<DiagnosticAnalyzer>();
        var assemblyPaths = ExpandBundledAnalyzerAssemblyPaths(bundledAnalyzerPaths);

        foreach (var path in assemblyPaths)
        {
            try
            {
                var assembly = Assembly.LoadFrom(path);
                var analyzerTypes = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t))
                    .Where(t => t.GetCustomAttribute<DiagnosticAnalyzerAttribute>() != null);

                foreach (var type in analyzerTypes)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is DiagnosticAnalyzer analyzer)
                            analyzers.Add(analyzer);
                    }
                    catch
                    {
                        // Skip analyzers that can't be instantiated.
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to load bundled analyzer assembly: {AnalyzerAssemblyPath}", path);
            }
        }

        return analyzers
            .GroupBy(a => a.GetType().FullName, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToImmutableArray();
    }

    private static IReadOnlyList<string> ExpandBundledAnalyzerAssemblyPaths(IReadOnlyList<string> configuredPaths)
    {
        var results = new List<string>();

        foreach (var raw in configuredPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());

            if (File.Exists(expanded) && expanded.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(Path.GetFullPath(expanded));
                continue;
            }

            if (Directory.Exists(expanded))
            {
                foreach (var dll in Directory.EnumerateFiles(expanded, "*.dll", SearchOption.TopDirectoryOnly))
                    results.Add(Path.GetFullPath(dll));
            }
        }

        return results;
    }

    private static ImmutableArray<DiagnosticAnalyzer> LoadBuiltInAnalyzers()
    {
        var analyzers = new List<DiagnosticAnalyzer>();

        // Load IDE analyzers from Microsoft.CodeAnalysis.CSharp.Features assembly
        try
        {
            var assembly = Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features");
            var analyzerTypes = assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t))
                .Where(t => t.GetCustomAttribute<DiagnosticAnalyzerAttribute>() != null);

            foreach (var type in analyzerTypes)
            {
                try
                {
                    if (Activator.CreateInstance(type) is DiagnosticAnalyzer analyzer)
                    {
                        // Only include analyzers that report our target diagnostic IDs
                        if (analyzer.SupportedDiagnostics.Any(d => UnusedDiagnosticIds.Contains(d.Id)))
                        {
                            analyzers.Add(analyzer);
                        }
                    }
                }
                catch
                {
                    // Skip analyzers that can't be instantiated
                }
            }
        }
        catch
        {
            // Assembly not available, skip it
        }

        return analyzers.ToImmutableArray();
    }

    public async Task<IReadOnlyList<Contracts.Diagnostic>> CollectUnusedDiagnosticsAsync(
        IReadOnlyList<Project> projects,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken)
    {
        var results = new List<Contracts.Diagnostic>();

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                    continue;

                // Get analyzer references from the project, or use built-in IDE analyzers
                var projectAnalyzers = GetAnalyzersToUse(project, compilation.Language);

                // If project has no analyzers, use our built-in analyzers for unused code detection
                var analyzersToUse = projectAnalyzers.IsEmpty ? BuiltInAnalyzers.Value : projectAnalyzers;

                ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> allDiagnostics;
                if (!analyzersToUse.IsEmpty)
                {
                    // Create CompilationWithAnalyzers to run analyzers
                    var compilationWithAnalyzers = compilation.WithAnalyzers(
                        analyzersToUse,
                        new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));

                    // Get all diagnostics (compiler + analyzer)
                    allDiagnostics = await compilationWithAnalyzers.GetAllDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // No analyzers available, just get compiler diagnostics
                    _logger.LogDebug("Project {ProjectName} has no analyzer references and built-in analyzers not loaded", project.Name);
                    allDiagnostics = compilation.GetDiagnostics(cancellationToken);
                }

                // Filter for unused diagnostics
                var unusedDiagnostics = allDiagnostics
                    .Where(d => UnusedDiagnosticIds.Contains(d.Id))
                    .Where(d => d.Location.IsInSource);

                // Apply folder/file scoping
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    var normalizedFolder = Path.GetFullPath(folderPath);
                    unusedDiagnostics = unusedDiagnostics
                        .Where(d => d.Location.SourceTree?.FilePath != null &&
                                   d.Location.SourceTree.FilePath.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase));
                }
                else if (!string.IsNullOrWhiteSpace(filePath))
                {
                    var normalizedFile = Path.GetFullPath(filePath);
                    unusedDiagnostics = unusedDiagnostics
                        .Where(d => d.Location.SourceTree?.FilePath != null &&
                                   string.Equals(d.Location.SourceTree.FilePath, normalizedFile, StringComparison.OrdinalIgnoreCase));
                }

                // Map to contract type
                foreach (var diagnostic in unusedDiagnostics)
                {
                    var lineSpan = diagnostic.Location.GetLineSpan();
                    results.Add(new Contracts.Diagnostic(
                        Id: diagnostic.Id,
                        Severity: diagnostic.Severity.ToString(),
                        Message: diagnostic.GetMessage(),
                        FilePath: diagnostic.Location.SourceTree?.FilePath ?? string.Empty,
                        StartLine: lineSpan.StartLinePosition.Line + 1,
                        StartColumn: lineSpan.StartLinePosition.Character + 1,
                        EndLine: lineSpan.EndLinePosition.Line + 1,
                        EndColumn: lineSpan.EndLinePosition.Character + 1));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to collect diagnostics from project {ProjectName}", project.Name);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<Contracts.Diagnostic>> CollectWarningDiagnosticsAsync(
        IReadOnlyList<Project> projects,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken)
    {
        var results = new List<Contracts.Diagnostic>();

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                    continue;

                // Get analyzer references from the project
                var analyzersToUse = GetAnalyzersToUse(project, compilation.Language);

                ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> allDiagnostics;
                if (!analyzersToUse.IsEmpty)
                {
                    // Create CompilationWithAnalyzers to run analyzers
                    var compilationWithAnalyzers = compilation.WithAnalyzers(
                        analyzersToUse,
                        new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));

                    // Get all diagnostics (compiler + analyzer)
                    allDiagnostics = await compilationWithAnalyzers.GetAllDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // No analyzers, just get compiler diagnostics
                    allDiagnostics = compilation.GetDiagnostics(cancellationToken);
                }

                // Filter for warning severity
                var warningDiagnostics = allDiagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Warning)
                    .Where(d => d.Location.IsInSource);

                // Apply folder/file scoping
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    var normalizedFolder = Path.GetFullPath(folderPath);
                    warningDiagnostics = warningDiagnostics
                        .Where(d => d.Location.SourceTree?.FilePath != null &&
                                   d.Location.SourceTree.FilePath.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase));
                }
                else if (!string.IsNullOrWhiteSpace(filePath))
                {
                    var normalizedFile = Path.GetFullPath(filePath);
                    warningDiagnostics = warningDiagnostics
                        .Where(d => d.Location.SourceTree?.FilePath != null &&
                                   string.Equals(d.Location.SourceTree.FilePath, normalizedFile, StringComparison.OrdinalIgnoreCase));
                }

                // Map to contract type
                foreach (var diagnostic in warningDiagnostics)
                {
                    var lineSpan = diagnostic.Location.GetLineSpan();
                    results.Add(new Contracts.Diagnostic(
                        Id: diagnostic.Id,
                        Severity: diagnostic.Severity.ToString(),
                        Message: diagnostic.GetMessage(),
                        FilePath: diagnostic.Location.SourceTree?.FilePath ?? string.Empty,
                        StartLine: lineSpan.StartLinePosition.Line + 1,
                        StartColumn: lineSpan.StartLinePosition.Character + 1,
                        EndLine: lineSpan.EndLinePosition.Line + 1,
                        EndColumn: lineSpan.EndLinePosition.Character + 1));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to collect warning diagnostics from project {ProjectName}", project.Name);
            }
        }

        return results;
    }
}
