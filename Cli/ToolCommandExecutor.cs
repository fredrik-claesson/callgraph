using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Diagnostics;
using CallGraph.Core.Extraction;
using CallGraph.Core.Indexing;
using CallGraph.Core.Output;
using CallGraph.Core.Search;
using CallGraph.Core.Solutions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using ContractDiagnostic = CallGraph.Contracts.Diagnostic;

namespace CallGraph.Cli;

internal sealed class ToolCommandExecutor
{
    private static readonly HashSet<string> SupportedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "install",
        "rewrite",
        "reindex",
        "list-solutions",
        "search-file",
        "search-method",
        "list-methods",
        "analyze",
        "get-method-source",
        "list-unused",
        "list-warnings"
    };

    private static readonly HashSet<string> DaemonPreferredCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "reindex",
        "list-solutions",
        "search-file",
        "search-method",
        "list-methods",
        "analyze",
        "get-method-source",
        "list-unused",
        "list-warnings"
    };

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
        var methodSourceExtractor = _services.GetRequiredService<IMethodSourceExtractor>();
        var hybridMethodSearch = _services.GetRequiredService<IHybridMethodSearchService>();
        var solutionLoader = _services.GetRequiredService<ISolutionLoader>();
        var solutionContextCache = _services.GetRequiredService<ISolutionContextCache>();

        switch (tool.Name)
        {
            case "install":
                return await InstallCommandRunner.RunAsync(tool, cancellationToken).ConfigureAwait(false);
            case "rewrite":
            {
                if (!TryGetRequired(tool.Options, "command", out var command, out var commandError))
                    return ToolExecutionResult.FromError(commandError!);

                if (!CommandRewriteEngine.TryRewrite(command, out var rewritten))
                    return ToolExecutionResult.FromError("No rewrite available.");

                return ToolExecutionResult.FromText(rewritten);
            }
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
                if (regex && !TryValidateRegexPattern(pattern, out var regexValidationError))
                    return ToolExecutionResult.FromError(regexValidationError!);

                var solutionPath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "solutionPath"));
                var solutionId = CliInputHelpers.TryGetString(tool.Options, "solutionId");
                var folderPath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "folderPath"));
                var filePath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "filePath"));

                var validateError = ValidateFolderOrFilePathExclusive(folderPath, filePath);
                if (validateError is not null)
                    return ToolExecutionResult.FromError(validateError);

                var matches = await _indexStore
                    .SearchFilesAsync(pattern, regex, solutionPath, solutionId, folderPath, filePath, cancellationToken)
                    .ConfigureAwait(false);

                if (solutionPath is null && solutionId is null)
                {
                    var distinctSolutionIds = matches
                        .Select(m => m.SolutionId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (distinctSolutionIds.Count > 1)
                        return ToolExecutionResult.FromError(
                            $"Results span {distinctSolutionIds.Count} indexed solutions. Use --solutionPath or --solutionId to scope to a single solution.");
                }

                const int limit = 200;
                if (matches.Count > limit)
                {
                    return ToolExecutionResult.FromError(
                        $"Search returned {matches.Count} results (limit {limit}). Narrow pattern or scope with --folderPath/--filePath.");
                }

                var response = ToolResponseMapper.ToSearchFileResponse(matches);
                return ToolExecutionResult.FromText(ToolTextFormatter.FormatSearchFiles(response));
            }
            case "search-method":
            {
                if (!TryGetPatternOrKeywords(tool.Options, out var queryText, out var queryError))
                    return ToolExecutionResult.FromError(queryError!);

                var regex = CliInputHelpers.TryGetBool(tool.Options, "regex", defaultValue: false, out var regexError);
                if (regexError is not null)
                    return ToolExecutionResult.FromError(regexError);
                if (regex && !TryValidateRegexPattern(queryText, out var regexValidationError))
                    return ToolExecutionResult.FromError(regexValidationError!);

                var solutionPath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "solutionPath"));
                var solutionId = CliInputHelpers.TryGetString(tool.Options, "solutionId");
                var folderPath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "folderPath"));
                var filePath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "filePath"));

                var validateError = ValidateFolderOrFilePathExclusive(folderPath, filePath);
                if (validateError is not null)
                    return ToolExecutionResult.FromError(validateError);

                var matches = await hybridMethodSearch
                    .SearchAsync(queryText, regex, solutionPath, solutionId, folderPath, filePath, cancellationToken)
                    .ConfigureAwait(false);

                if (solutionPath is null && solutionId is null)
                {
                    var distinctSolutionIds = matches
                        .Select(m => m.SolutionId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (distinctSolutionIds.Count > 1)
                        return ToolExecutionResult.FromError(
                            $"Results span {distinctSolutionIds.Count} indexed solutions. Use --solutionPath or --solutionId to scope to a single solution.");
                }

                const int limit = 200;
                if (matches.Count > limit)
                {
                    return ToolExecutionResult.FromError(
                        $"Search returned {matches.Count} results (limit {limit}). Narrow pattern or scope with --folderPath/--filePath.");
                }

                var response = ToolResponseMapper.ToSearchMethodResponse(matches);
                return ToolExecutionResult.FromText(ToolTextFormatter.FormatSearchMethods(response));
            }
            case "list-methods":
            {
                var visibility = NormalizeVisibility(CliInputHelpers.TryGetString(tool.Options, "visibility") ?? "external", out var visibilityError);
                if (visibilityError is not null)
                    return ToolExecutionResult.FromError(visibilityError);
                if (visibility is null)
                    return ToolExecutionResult.FromError("visibility must be internal or external.");

                var solutionPath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "solutionPath"));
                var solutionId = CliInputHelpers.TryGetString(tool.Options, "solutionId");
                var folderPath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "folderPath"));
                var filePath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "filePath"));
                var fileListPath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "fileList"));

                var fileList = LoadFileList(fileListPath, out var fileListError);
                if (fileListError is not null)
                    return ToolExecutionResult.FromError(fileListError);

                var validateError = ValidateMethodListScopes(folderPath, filePath, fileList);
                if (validateError is not null)
                    return ToolExecutionResult.FromError(validateError);

                const int limit = 200;
                var liveMatches = await BuildLiveListMethodMatchesAsync(
                        visibility,
                        solutionPath,
                        solutionId,
                        folderPath,
                        filePath,
                        fileList,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (solutionPath is null && solutionId is null)
                {
                    var distinctSolutionIds = liveMatches
                        .Select(m => m.SolutionId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (distinctSolutionIds.Count > 1)
                        return ToolExecutionResult.FromError(
                            $"Results span {distinctSolutionIds.Count} indexed solutions. Use --solutionPath or --solutionId to scope to a single solution.");
                }

                if (liveMatches.Count > limit)
                {
                    return ToolExecutionResult.FromError(
                        $"List returned {liveMatches.Count} results (limit {limit}). Narrow scope with --solutionPath/--solutionId and --folderPath/--filePath.");
                }

                var response = ToolResponseMapper.ToSearchMethodResponse(liveMatches);
                return ToolExecutionResult.FromText(ToolTextFormatter.FormatSearchMethods(response));
            }
            case "analyze":
            {
                if (!TryGetRequired(tool.Options, "filepath", out var filepath, out var filepathError))
                    return ToolExecutionResult.FromError(filepathError!);
                filepath = NormalizeRequiredPath(filepath);

                var method = CliInputHelpers.TryGetString(tool.Options, "method");
                var depth = CliInputHelpers.TryGetInt(tool.Options, "depth", out var depthError) ?? 1;
                if (depthError is not null)
                    return ToolExecutionResult.FromError(depthError);

                var direction = CliInputHelpers.TryGetString(tool.Options, "direction");
                var visibility = NormalizeVisibility(CliInputHelpers.TryGetString(tool.Options, "visibility") ?? "external", out var visibilityError);
                if (visibilityError is not null)
                    return ToolExecutionResult.FromError(visibilityError);

                var solutionPath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "solutionPath"));
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
                if (result.Graph is null)
                    return ToolExecutionResult.FromError(result.Error?.Detail ?? "Analyze failed.");

                var response = ToolResponseMapper.ToAnalyzeResponse(result.Graph);
                return ToolExecutionResult.FromText(ToolTextFormatter.FormatAnalyze(response));
            }
            case "get-method-source":
            {
                if (!TryGetRequired(tool.Options, "filePath", out var filePath, out var filePathError))
                    return ToolExecutionResult.FromError(filePathError!);
                filePath = NormalizeRequiredPath(filePath);

                if (!Path.IsPathRooted(filePath))
                    return ToolExecutionResult.FromError("filePath must be an absolute path.");

                if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    return ToolExecutionResult.FromError("filePath must point to a .cs file.");

                var mode = CliInputHelpers.TryGetString(tool.Options, "mode");
                var methodName = CliInputHelpers.TryGetString(tool.Options, "methodName") ??
                                 CliInputHelpers.TryGetString(tool.Options, "method");
                var containingType = CliInputHelpers.TryGetString(tool.Options, "containingType");
                var signature = CliInputHelpers.TryGetString(tool.Options, "signature");
                var startLine = CliInputHelpers.TryGetInt(tool.Options, "startLine", out var startLineError);
                if (startLineError is not null)
                    return ToolExecutionResult.FromError(startLineError);

                var extraction = await methodSourceExtractor
                    .ExtractAsync(
                        new MethodSourceExtractionRequest(
                            FilePath: filePath,
                            MethodName: methodName,
                            ContainingType: containingType,
                            Signature: signature,
                            StartLine: startLine,
                            Mode: mode ?? "signature_plus_body"),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!extraction.Success)
                {
                    if (extraction.Candidates is { Count: > 0 })
                    {
                        var candidateLines = string.Join(
                            Environment.NewLine,
                            extraction.Candidates.Select(c =>
                                $"{c.StartLine}-{c.EndLine}\t{c.ContainingType ?? "-"}\t{c.MethodName}\t{c.Signature}"));

                        return ToolExecutionResult.FromError($"{extraction.Error}{Environment.NewLine}{candidateLines}");
                    }

                    return ToolExecutionResult.FromError(extraction.Error ?? "Failed to extract method source.");
                }

                return ToolExecutionResult.FromPayload(extraction.Match!, JsonOutputOptions);
            }
            case "list-unused":
            {
                if (!TryGetRequired(tool.Options, "projectPath", out var projectPath, out var projectError))
                    return ToolExecutionResult.FromError(projectError!);
                projectPath = NormalizeRequiredPath(projectPath);

                var folderPath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "folderPath"));
                var filePath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "filePath"));

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
                        ToolResponseMapper.ToDiagnosticResponse(resultDiagnostics, totalCount),
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
                projectPath = NormalizeRequiredPath(projectPath);

                var folderPath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "folderPath"));
                var filePath = NormalizeOptionalPath(CliInputHelpers.TryGetString(tool.Options, "filePath"));

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
                        ToolResponseMapper.ToDiagnosticResponse(resultDiagnostics, totalCount),
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

    private static string? NormalizeOptionalPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return rawPath;

        return Path.GetFullPath(rawPath);
    }

    private static string NormalizeRequiredPath(string rawPath)
        => Path.GetFullPath(rawPath);

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

    private static string? ValidateMethodListScopes(string? folderPath, string? filePath, IReadOnlyList<string> fileList)
    {
        var scopeError = ValidateFolderOrFilePathExclusive(folderPath, filePath);
        if (scopeError is not null)
            return scopeError;

        var hasFolderPath = !string.IsNullOrWhiteSpace(folderPath);
        var hasFilePath = !string.IsNullOrWhiteSpace(filePath);
        var hasFileList = fileList.Count > 0;
        if ((hasFolderPath && hasFileList) || (hasFilePath && hasFileList))
            return "Provide only one scope option: --folderPath or --filePath or --fileList.";

        return null;
    }

    private static IReadOnlyList<string> LoadFileList(string? fileListPath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(fileListPath))
            return Array.Empty<string>();

        var normalizedPath = Path.GetFullPath(fileListPath);
        if (!File.Exists(normalizedPath))
        {
            error = $"fileList not found: {fileListPath}";
            return Array.Empty<string>();
        }

        var lines = File.ReadAllLines(normalizedPath);
        var entries = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0 || raw.StartsWith('#'))
                continue;

            if (!Path.IsPathRooted(raw))
            {
                error = $"fileList line {i + 1}: path must be absolute.";
                return Array.Empty<string>();
            }

            if (!raw.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                error = $"fileList line {i + 1}: path must point to a .cs file.";
                return Array.Empty<string>();
            }

            entries.Add(Path.GetFullPath(raw));
        }

        return entries
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private static bool TryValidateRegexPattern(string pattern, out string? error)
    {
        error = null;

        try
        {
            _ = Regex.IsMatch(string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return true;
        }
        catch (ArgumentException ex)
        {
            error =
                $"Invalid regex pattern '{pattern}': {ex.Message}. If you intended wildcard matching, remove --regex and use '*' or '?' instead.";
            return false;
        }
    }

    private sealed record WarningDiagnosticsCacheEntry(
        DateTime CreatedUtc,
        IReadOnlyList<ContractDiagnostic> Diagnostics);

    private async Task<IReadOnlyList<SearchMethodMatch>> BuildLiveListMethodMatchesAsync(
        string visibility,
        string? solutionPath,
        string? solutionId,
        string? folderPath,
        string? filePath,
        IReadOnlyList<string> fileList,
        CancellationToken cancellationToken)
    {
        if (fileList.Count > 0)
        {
            var fileListMatches = new List<SearchMethodMatch>();
            foreach (var rawFilePath in fileList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalizedFilePath = Path.GetFullPath(rawFilePath);
                if (!File.Exists(normalizedFilePath))
                    continue;

                var nodes = await ListLiveMethodNodesInFileAsync(normalizedFilePath, visibility, cancellationToken).ConfigureAwait(false);
                var (resolvedSolutionId, resolvedSolutionPath) = await ResolveSolutionIdentityForFileAsync(
                    normalizedFilePath,
                    solutionPath,
                    solutionId,
                    cancellationToken).ConfigureAwait(false);

                fileListMatches.AddRange(nodes.Select(node =>
                    new SearchMethodMatch(resolvedSolutionId, resolvedSolutionPath, node)));
            }

            return fileListMatches
                .OrderBy(match => match.Method.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.Method.StartLine ?? int.MaxValue)
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var normalizedFilePath = Path.GetFullPath(filePath);
            if (!File.Exists(normalizedFilePath))
                return Array.Empty<SearchMethodMatch>();

            var nodes = await ListLiveMethodNodesInFileAsync(normalizedFilePath, visibility, cancellationToken).ConfigureAwait(false);
            var (resolvedSolutionId, resolvedSolutionPath) = await ResolveSolutionIdentityForFileAsync(
                normalizedFilePath,
                solutionPath,
                solutionId,
                cancellationToken).ConfigureAwait(false);

            return nodes
                .Select(node => new SearchMethodMatch(resolvedSolutionId, resolvedSolutionPath, node))
                .OrderBy(match => match.Method.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.Method.StartLine ?? int.MaxValue)
                .ToList();
        }

        var discoveryMatches = await _indexStore
            .ListMethodsAsync("internal", solutionPath, solutionId, folderPath, filePath, cancellationToken)
            .ConfigureAwait(false);

        if (discoveryMatches.Count == 0)
            return Array.Empty<SearchMethodMatch>();

        var fileToSolutionMap = new Dictionary<string, (string SolutionId, string SolutionPath)>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in discoveryMatches)
        {
            var candidateFilePath = match.Method.FilePath;
            if (string.IsNullOrWhiteSpace(candidateFilePath) ||
                !Path.IsPathRooted(candidateFilePath))
            {
                continue;
            }

            if (fileToSolutionMap.ContainsKey(candidateFilePath))
                continue;

            fileToSolutionMap[candidateFilePath] = (match.SolutionId, match.SolutionPath);
        }

        var liveMatches = new List<SearchMethodMatch>();
        foreach (var (candidateFilePath, solution) in fileToSolutionMap)
        {
            if (!File.Exists(candidateFilePath))
                continue;

            var nodes = await ListLiveMethodNodesInFileAsync(candidateFilePath, visibility, cancellationToken).ConfigureAwait(false);
            foreach (var node in nodes)
            {
                liveMatches.Add(new SearchMethodMatch(solution.SolutionId, solution.SolutionPath, node));
            }
        }

        return liveMatches
            .OrderBy(match => match.Method.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Method.StartLine ?? int.MaxValue)
            .ToList();
    }

    private async Task<(string SolutionId, string SolutionPath)> ResolveSolutionIdentityForFileAsync(
        string filePath,
        string? solutionPath,
        string? solutionId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(solutionId))
        {
            var solution = await _indexStore.GetSolutionByIdAsync(solutionId, cancellationToken).ConfigureAwait(false);
            return (solutionId, solution?.SolutionPath ?? string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            var normalizedSolutionPath = Path.GetFullPath(solutionPath);
            var solution = await _indexStore.GetSolutionByPathAsync(normalizedSolutionPath, cancellationToken).ConfigureAwait(false);
            return (solution?.SolutionId ?? string.Empty, normalizedSolutionPath);
        }

        var solutions = await _indexStore.FindSolutionsByFilePathAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (solutions.Count == 1)
            return (solutions[0].SolutionId, solutions[0].SolutionPath);

        return (string.Empty, string.Empty);
    }

    private static async Task<IReadOnlyList<Node>> ListLiveMethodNodesInFileAsync(
        string filePath,
        string visibility,
        CancellationToken cancellationToken)
    {
        var source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: cancellationToken);
        var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);

        var methods = new List<Node>();
        foreach (var declaration in CallableSyntax.EnumerateDeclarations(root))
        {
            var accessibility = CallableSyntax.GetAccessibility(declaration);
            if (!IsVisibilityMatch(accessibility, visibility))
                continue;

            var signature = CallableSyntax.ExtractSignatureText(declaration, source);
            if (string.IsNullOrWhiteSpace(signature))
                continue;

            var lineSpan = syntaxTree.GetLineSpan(declaration.Span);
            var startLine = lineSpan.StartLinePosition.Line + 1;
            var methodName = CallableSyntax.ExtractMethodName(declaration);

            methods.Add(new Node
            {
                Id = $"{filePath}:{startLine}:{methodName}",
                Kind = CallableSyntax.GetCallableKind(declaration),
                Display = signature.TrimEnd(),
                ContainingType = CallableSyntax.ExtractContainingType(declaration),
                FilePath = filePath,
                StartLine = startLine,
                Accessibility = accessibility
            });
        }

        return methods;
    }

    private static bool IsVisibilityMatch(string accessibility, string visibility)
    {
        if (string.Equals(visibility, "internal", StringComparison.OrdinalIgnoreCase))
            return true;

        return accessibility switch
        {
            "public" => true,
            "protected" => true,
            "protected internal" => true,
            _ => false
        };
    }

}

internal sealed record ToolExecutionResult(int ExitCode, string? Stdout, string? Stderr)
{
    public static ToolExecutionResult FromText(string? text)
        => new(0, text, null);

    public static ToolExecutionResult FromPayload(object payload, JsonSerializerOptions options)
        => new(0, JsonSerializer.Serialize(payload, options), null);

    public static ToolExecutionResult FromError(string message)
        => new(1, null, message);
}

internal sealed record ToolCommand(string Name, IReadOnlyDictionary<string, string?> Options);
