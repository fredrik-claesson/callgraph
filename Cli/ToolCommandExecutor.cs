using System.Collections.Concurrent;
using System.Text.Json;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Diagnostics;
using CallGraph.Core.Indexing;
using CallGraph.Core.Output;
using CallGraph.Core.Search;
using CallGraph.Core.Solutions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using ContractDiagnostic = CallGraph.Contracts.Diagnostic;

namespace CallGraph.Cli;

internal sealed class ToolCommandExecutor
{
    private static readonly HashSet<string> SupportedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "reindex",
        "list-solutions",
        "search-file",
        "search-method",
        "list-methods",
        "analyze",
        "list-unused",
        "list-warnings"
    };

    private static readonly HashSet<string> DaemonPreferredCommands = new(SupportedCommands, StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan WarningDiagnosticsCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, WarningDiagnosticsCacheEntry> WarningDiagnosticsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WarningDiagnosticsCacheLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IServiceProvider _services;
    private readonly IIndexStore _indexStore;

    public ToolCommandExecutor(IServiceProvider services, IIndexStore indexStore)
    {
        _services = services;
        _indexStore = indexStore;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(ToolCommand tool, CancellationToken cancellationToken)
    {
        if (!SupportsCommand(tool.Name))
            return ToolExecutionResult.FromError($"Unknown command: {tool.Name}");

        var graphAnalyzer = _services.GetRequiredService<IGraphAnalyzer>();
        var diagnosticCollector = _services.GetRequiredService<IDiagnosticCollector>();
        var hybridMethodSearch = _services.GetRequiredService<IHybridMethodSearchService>();
        var solutionLoader = _services.GetRequiredService<ISolutionLoader>();
        var solutionContextCache = _services.GetRequiredService<ISolutionContextCache>();

        switch (tool.Name)
        {
            case "reindex":
            {
                if (!TryGetRequired(tool.Options, "solutionPath", out var solutionPath, out var solutionPathError))
                    return ToolExecutionResult.FromError(solutionPathError!);

                var normalizedPath = CliInputHelpers.NormalizeSolutionPath(solutionPath, "--solutionPath");
                if (normalizedPath.Error is not null || string.IsNullOrWhiteSpace(normalizedPath.Path))
                    return ToolExecutionResult.FromError(normalizedPath.Error ?? "Invalid --solutionPath.");

                var slnOnly = CliInputHelpers.TryGetBool(tool.Options, "slnOnly", defaultValue: true, out var slnOnlyError);
                if (slnOnlyError is not null)
                    return ToolExecutionResult.FromError(slnOnlyError);

                var pipeline = _services.GetRequiredService<IIndexingPipeline>();
                var normalizedSolutionPath = normalizedPath.Path;
                var solutionId = SolutionIdentity.FromPath(normalizedSolutionPath);
                var request = new IndexJobRequest(
                    Guid.NewGuid().ToString("N"),
                    solutionId,
                    normalizedSolutionPath,
                    slnOnly,
                    IsReindex: true);

                await pipeline.RunAsync(request, cancellationToken).ConfigureAwait(false);
                return new ToolExecutionResult(0, null, null);
            }
            case "list-solutions":
            {
                var solutions = await _indexStore.ListSolutionsAsync(cancellationToken).ConfigureAwait(false);
                return ToolExecutionResult.FromPayload(new { solutions }, JsonOutputOptions);
            }
            case "search-file":
            {
                if (!TryGetRequired(tool.Options, "pattern", out var pattern, out var patternError))
                    return ToolExecutionResult.FromError(patternError!);

                var regex = CliInputHelpers.TryGetBool(tool.Options, "regex", defaultValue: false, out var regexError);
                if (regexError is not null)
                    return ToolExecutionResult.FromError(regexError);

                var solutionPath = CliInputHelpers.TryGetString(tool.Options, "solutionPath");
                var solutionId = CliInputHelpers.TryGetString(tool.Options, "solutionId");
                var folderPath = CliInputHelpers.TryGetString(tool.Options, "folderPath");
                var filePath = CliInputHelpers.TryGetString(tool.Options, "filePath");

                var validateError = ValidateFolderOrFilePathExclusive(folderPath, filePath);
                if (validateError is not null)
                    return ToolExecutionResult.FromError(validateError);

                var matches = await _indexStore
                    .SearchFilesAsync(pattern, regex, solutionPath, solutionId, folderPath, filePath, cancellationToken)
                    .ConfigureAwait(false);

                const int limit = 200;
                if (matches.Count > limit)
                {
                    return ToolExecutionResult.FromError(
                        $"Search returned {matches.Count} results (limit {limit}). Narrow pattern or scope with --folderPath/--filePath.");
                }

                return ToolExecutionResult.FromPayload(ToolResponseMapper.ToSearchFileResponse(matches), JsonOutputOptions);
            }
            case "search-method":
            {
                if (!TryGetPatternOrKeywords(tool.Options, out var queryText, out var queryError))
                    return ToolExecutionResult.FromError(queryError!);

                var regex = CliInputHelpers.TryGetBool(tool.Options, "regex", defaultValue: false, out var regexError);
                if (regexError is not null)
                    return ToolExecutionResult.FromError(regexError);

                var solutionPath = CliInputHelpers.TryGetString(tool.Options, "solutionPath");
                var solutionId = CliInputHelpers.TryGetString(tool.Options, "solutionId");
                var folderPath = CliInputHelpers.TryGetString(tool.Options, "folderPath");
                var filePath = CliInputHelpers.TryGetString(tool.Options, "filePath");

                var validateError = ValidateFolderOrFilePathExclusive(folderPath, filePath);
                if (validateError is not null)
                    return ToolExecutionResult.FromError(validateError);

                var matches = await hybridMethodSearch
                    .SearchAsync(queryText, regex, solutionPath, solutionId, folderPath, filePath, cancellationToken)
                    .ConfigureAwait(false);

                const int limit = 200;
                if (matches.Count > limit)
                {
                    return ToolExecutionResult.FromError(
                        $"Search returned {matches.Count} results (limit {limit}). Narrow pattern or scope with --folderPath/--filePath.");
                }

                return ToolExecutionResult.FromPayload(ToolResponseMapper.ToSearchMethodResponse(matches), JsonOutputOptions);
            }
            case "list-methods":
            {
                var visibility = NormalizeVisibility(CliInputHelpers.TryGetString(tool.Options, "visibility") ?? "external", out var visibilityError);
                if (visibilityError is not null)
                    return ToolExecutionResult.FromError(visibilityError);
                if (visibility is null)
                    return ToolExecutionResult.FromError("visibility must be internal or external.");

                var solutionPath = CliInputHelpers.TryGetString(tool.Options, "solutionPath");
                var solutionId = CliInputHelpers.TryGetString(tool.Options, "solutionId");
                var folderPath = CliInputHelpers.TryGetString(tool.Options, "folderPath");
                var filePath = CliInputHelpers.TryGetString(tool.Options, "filePath");

                var validateError = ValidateFolderOrFilePathExclusive(folderPath, filePath);
                if (validateError is not null)
                    return ToolExecutionResult.FromError(validateError);

                var matches = await _indexStore
                    .ListMethodsAsync(visibility, solutionPath, solutionId, folderPath, filePath, cancellationToken)
                    .ConfigureAwait(false);

                const int limit = 200;
                if (matches.Count > limit)
                {
                    return ToolExecutionResult.FromError(
                        $"List returned {matches.Count} results (limit {limit}). Narrow scope with --solutionPath/--solutionId and --folderPath/--filePath.");
                }

                return ToolExecutionResult.FromPayload(ToolResponseMapper.ToSearchMethodResponse(matches), JsonOutputOptions);
            }
            case "analyze":
            {
                if (!TryGetRequired(tool.Options, "filepath", out var filepath, out var filepathError))
                    return ToolExecutionResult.FromError(filepathError!);

                var method = CliInputHelpers.TryGetString(tool.Options, "method");
                var depth = CliInputHelpers.TryGetInt(tool.Options, "depth", out var depthError) ?? 1;
                if (depthError is not null)
                    return ToolExecutionResult.FromError(depthError);

                var direction = CliInputHelpers.TryGetString(tool.Options, "direction");
                var visibility = NormalizeVisibility(CliInputHelpers.TryGetString(tool.Options, "visibility") ?? "external", out var visibilityError);
                if (visibilityError is not null)
                    return ToolExecutionResult.FromError(visibilityError);

                var solutionPath = CliInputHelpers.TryGetString(tool.Options, "solutionPath");
                var solutionId = CliInputHelpers.TryGetString(tool.Options, "solutionId");

                var request = new AnalyzeRequest(
                    FilePath: filepath,
                    Depth: depth,
                    Method: method,
                    SolutionPath: solutionPath,
                    SolutionId: solutionId,
                    Direction: direction,
                    Visibility: visibility);

                var result = await graphAnalyzer.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
                object payload = result.Graph is not null
                    ? ToolResponseMapper.ToAnalyzeResponse(result.Graph)
                    : new { error = result.Error };

                return ToolExecutionResult.FromPayload(payload, JsonOutputOptions);
            }
            case "list-unused":
            {
                if (!TryGetRequired(tool.Options, "projectPath", out var projectPath, out var projectError))
                    return ToolExecutionResult.FromError(projectError!);

                var folderPath = CliInputHelpers.TryGetString(tool.Options, "folderPath");
                var filePath = CliInputHelpers.TryGetString(tool.Options, "filePath");

                if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    return ToolExecutionResult.FromError("projectPath must point to a .csproj file.");

                var validateError = ValidateRequiredCsFilePathForDiagnostics(folderPath, filePath);
                if (validateError is not null)
                    return ToolExecutionResult.FromError(validateError);

                var resolveProject = await ResolveProjectPathAsync(projectPath, _indexStore, cancellationToken).ConfigureAwait(false);
                if (resolveProject.Error is not null)
                    return ToolExecutionResult.FromError(resolveProject.Error);

                var (projects, disposable, loadError) = await LoadProjectsForDiagnosticsAsync(
                    resolveProject.ProjectPath!,
                    _indexStore,
                    solutionContextCache,
                    solutionLoader,
                    cancellationToken).ConfigureAwait(false);

                if (loadError is not null)
                    return ToolExecutionResult.FromError(loadError);

                try
                {
                    var diagnostics = await diagnosticCollector.CollectUnusedDiagnosticsAsync(
                        projects,
                        folderPath: null,
                        filePath,
                        cancellationToken).ConfigureAwait(false);

                    const int limit = 100;
                    var totalCount = diagnostics.Count;
                    var resultDiagnostics = totalCount > limit ? diagnostics.Take(limit).ToList() : diagnostics;
                    return ToolExecutionResult.FromPayload(
                        ToolResponseMapper.ToDiagnosticResponse(resultDiagnostics, totalCount, limit),
                        JsonOutputOptions);
                }
                finally
                {
                    if (disposable is not null)
                        await disposable.DisposeAsync().ConfigureAwait(false);
                }
            }
            case "list-warnings":
            {
                if (!TryGetRequired(tool.Options, "projectPath", out var projectPath, out var projectError))
                    return ToolExecutionResult.FromError(projectError!);

                var folderPath = CliInputHelpers.TryGetString(tool.Options, "folderPath");
                var filePath = CliInputHelpers.TryGetString(tool.Options, "filePath");

                if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    return ToolExecutionResult.FromError("projectPath must point to a .csproj file.");

                var validateError = ValidateRequiredCsFilePathForDiagnostics(folderPath, filePath);
                if (validateError is not null)
                    return ToolExecutionResult.FromError(validateError);

                var resolveProject = await ResolveProjectPathAsync(projectPath, _indexStore, cancellationToken).ConfigureAwait(false);
                if (resolveProject.Error is not null)
                    return ToolExecutionResult.FromError(resolveProject.Error);

                var (projects, disposable, loadError) = await LoadProjectsForDiagnosticsAsync(
                    resolveProject.ProjectPath!,
                    _indexStore,
                    solutionContextCache,
                    solutionLoader,
                    cancellationToken).ConfigureAwait(false);

                if (loadError is not null)
                    return ToolExecutionResult.FromError(loadError);

                try
                {
                    var allProjectWarnings = await GetOrCollectWarningDiagnosticsCachedAsync(
                        resolveProject.ProjectPath!,
                        projects,
                        diagnosticCollector,
                        cancellationToken).ConfigureAwait(false);

                    var diagnostics = FilterDiagnosticsByScope(allProjectWarnings, folderPath: null, filePath);

                    const int limit = 500;
                    var totalCount = diagnostics.Count;
                    var resultDiagnostics = totalCount > limit ? diagnostics.Take(limit).ToList() : diagnostics;
                    return ToolExecutionResult.FromPayload(
                        ToolResponseMapper.ToDiagnosticResponse(resultDiagnostics, totalCount, limit),
                        JsonOutputOptions);
                }
                finally
                {
                    if (disposable is not null)
                        await disposable.DisposeAsync().ConfigureAwait(false);
                }
            }
            default:
                return ToolExecutionResult.FromError($"Unknown command: {tool.Name}");
        }
    }

    internal static bool SupportsCommand(string commandName)
        => SupportedCommands.Contains(commandName);

    internal static bool ShouldUseDaemonByDefault(string commandName)
        => DaemonPreferredCommands.Contains(commandName);

    private static async Task<IReadOnlyList<ContractDiagnostic>> GetOrCollectWarningDiagnosticsCachedAsync(
        string projectPath,
        IReadOnlyList<Project> projects,
        IDiagnosticCollector diagnosticCollector,
        CancellationToken cancellationToken)
    {
        var normalizedProjectPath = Path.GetFullPath(projectPath);
        var nowUtc = DateTime.UtcNow;

        if (WarningDiagnosticsCache.TryGetValue(normalizedProjectPath, out var cached) &&
            (nowUtc - cached.CreatedUtc) <= WarningDiagnosticsCacheTtl)
        {
            return cached.Diagnostics;
        }

        var cacheLock = WarningDiagnosticsCacheLocks.GetOrAdd(
            normalizedProjectPath,
            static _ => new SemaphoreSlim(1, 1));

        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nowUtc = DateTime.UtcNow;
            if (WarningDiagnosticsCache.TryGetValue(normalizedProjectPath, out cached) &&
                (nowUtc - cached.CreatedUtc) <= WarningDiagnosticsCacheTtl)
            {
                return cached.Diagnostics;
            }

            var collected = await diagnosticCollector
                .CollectWarningDiagnosticsAsync(projects, folderPath: null, filePath: null, cancellationToken)
                .ConfigureAwait(false);

            var snapshot = collected as List<ContractDiagnostic> ?? collected.ToList();
            WarningDiagnosticsCache[normalizedProjectPath] = new WarningDiagnosticsCacheEntry(nowUtc, snapshot);
            TrimExpiredWarningDiagnosticsCacheEntries(nowUtc);
            return snapshot;
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private static List<ContractDiagnostic> FilterDiagnosticsByScope(
        IReadOnlyList<ContractDiagnostic> diagnostics,
        string? folderPath,
        string? filePath)
    {
        IEnumerable<ContractDiagnostic> query = diagnostics;

        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            var normalizedFolder = Path.GetFullPath(folderPath);
            query = query.Where(d =>
                !string.IsNullOrWhiteSpace(d.FilePath) &&
                d.FilePath.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(filePath))
        {
            var normalizedFile = Path.GetFullPath(filePath);
            query = query.Where(d =>
                !string.IsNullOrWhiteSpace(d.FilePath) &&
                string.Equals(d.FilePath, normalizedFile, StringComparison.OrdinalIgnoreCase));
        }

        return query.ToList();
    }

    private static void TrimExpiredWarningDiagnosticsCacheEntries(DateTime nowUtc)
    {
        foreach (var kvp in WarningDiagnosticsCache)
        {
            if ((nowUtc - kvp.Value.CreatedUtc) > WarningDiagnosticsCacheTtl)
                WarningDiagnosticsCache.TryRemove(kvp.Key, out _);
        }
    }

    private static async Task<(IReadOnlyList<Project> Projects, IAsyncDisposable? Disposable, string? Error)> LoadProjectsForDiagnosticsAsync(
        string projectPath,
        IIndexStore indexStore,
        ISolutionContextCache solutionContextCache,
        ISolutionLoader solutionLoader,
        CancellationToken cancellationToken)
    {
        var normalizedProjectPath = Path.GetFullPath(projectPath);

        try
        {
            var matches = await indexStore.FindProjectsByPathSuffixAsync(normalizedProjectPath, cancellationToken).ConfigureAwait(false);
            var exactMatches = matches.Where(m => CliInputHelpers.PathsEqual(m.ProjectPath, normalizedProjectPath)).ToList();

            if (exactMatches.Count == 1)
            {
                var solution = exactMatches[0].Solution;
                var cachedContext = await solutionContextCache
                    .GetOrLoadAsync(solution.SolutionPath, solution.SlnOnly, cancellationToken)
                    .ConfigureAwait(false);

                var project = cachedContext.Projects.FirstOrDefault(
                    p => p.FilePath is not null && CliInputHelpers.PathsEqual(p.FilePath, normalizedProjectPath));

                if (project is not null)
                    return (new List<Project> { project }, null, null);
            }

            var projectContext = await solutionLoader.LoadProjectAsync(normalizedProjectPath, cancellationToken).ConfigureAwait(false);
            return (projectContext.Projects, projectContext, null);
        }
        catch (Exception ex)
        {
            return (Array.Empty<Project>(), null, $"Failed to load project: {ex.Message}");
        }
    }

    private static async Task<(string? ProjectPath, string? Error)> ResolveProjectPathAsync(
        string projectPath,
        IIndexStore indexStore,
        CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(projectPath))
        {
            if (!File.Exists(projectPath))
                return (null, $"Project file not found: {projectPath}");

            return (Path.GetFullPath(projectPath), null);
        }

        var matches = await indexStore.FindProjectsByPathSuffixAsync(projectPath, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return (null,
                $"No indexed projects found matching '{projectPath}'. Ensure the solution containing this project has been indexed.");
        }

        if (matches.Count > 1)
        {
            var candidates = string.Join(Environment.NewLine,
                matches.Select(m => $"  - {m.ProjectPath} (solution: {m.Solution.SolutionPath})"));
            return (null,
                $"Multiple projects match '{projectPath}'. Use an absolute path or provide more path segments to disambiguate.{Environment.NewLine}{candidates}");
        }

        return (matches[0].ProjectPath, null);
    }

    private static bool TryGetRequired(
        IReadOnlyDictionary<string, string?> options,
        string key,
        out string value,
        out string? error)
    {
        if (!options.TryGetValue(key, out var candidate) || string.IsNullOrWhiteSpace(candidate))
        {
            error = $"Missing required option: --{key}";
            value = string.Empty;
            return false;
        }

        value = candidate;
        error = null;
        return true;
    }

    private static bool TryGetPatternOrKeywords(
        IReadOnlyDictionary<string, string?> options,
        out string value,
        out string? error)
    {
        var keywords = CliInputHelpers.TryGetString(options, "keywords");
        var pattern = CliInputHelpers.TryGetString(options, "pattern");

        if (keywords is not null && pattern is not null && !string.Equals(keywords, pattern, StringComparison.Ordinal))
        {
            error = "Provide either --keywords or --pattern (alias), not both with different values.";
            value = string.Empty;
            return false;
        }

        if (keywords is not null)
        {
            value = keywords;
            error = null;
            return true;
        }

        if (pattern is not null)
        {
            value = pattern;
            error = null;
            return true;
        }

        error = "Missing required option: --keywords (or --pattern alias).";
        value = string.Empty;
        return false;
    }

    private static string? NormalizeVisibility(string visibility, out string? error)
    {
        var trimmed = visibility.Trim();
        if (string.Equals(trimmed, "internal", StringComparison.OrdinalIgnoreCase))
        {
            error = null;
            return "internal";
        }

        if (string.Equals(trimmed, "external", StringComparison.OrdinalIgnoreCase))
        {
            error = null;
            return "external";
        }

        error = "visibility must be internal or external.";
        return null;
    }

    private static string? ValidateFolderOrFilePathExclusive(string? folderPath, string? filePath)
    {
        var hasFolderPath = !string.IsNullOrWhiteSpace(folderPath);
        var hasFilePath = !string.IsNullOrWhiteSpace(filePath);

        if (hasFolderPath && hasFilePath)
            return "Provide only --folderPath or --filePath, not both.";

        if (hasFolderPath && !Path.IsPathRooted(folderPath!))
            return "folderPath must be an absolute path.";

        if (hasFilePath && !Path.IsPathRooted(filePath!))
            return "filePath must be an absolute path.";

        if (hasFilePath && !filePath!.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return "filePath must point to a .cs file.";

        return null;
    }

    private static string? ValidateRequiredCsFilePathForDiagnostics(string? folderPath, string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(folderPath))
            return "folderPath is not supported for this command. Provide --filePath <absolute .cs file>.";

        if (string.IsNullOrWhiteSpace(filePath))
            return "Missing required option: --filePath";

        if (!Path.IsPathRooted(filePath))
            return "filePath must be an absolute path.";

        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return "filePath must point to a .cs file.";

        return null;
    }

    private sealed record WarningDiagnosticsCacheEntry(
        DateTime CreatedUtc,
        IReadOnlyList<ContractDiagnostic> Diagnostics);
}

internal sealed record ToolExecutionResult(int ExitCode, string? Stdout, string? Stderr)
{
    public static ToolExecutionResult FromPayload(object payload, JsonSerializerOptions options)
        => new(0, JsonSerializer.Serialize(payload, options), null);

    public static ToolExecutionResult FromError(string message)
        => new(1, null, message);
}

internal sealed record ToolCommand(string Name, IReadOnlyDictionary<string, string?> Options);
