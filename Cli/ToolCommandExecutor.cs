using System.Text.Json;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Indexing;
using CallGraph.Core.Output;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CallGraph.Cli;

internal sealed class ToolCommandExecutor
{
    private static readonly HashSet<string> SupportedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "analyze",
        "query"
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

        switch (tool.Name)
        {
            case "query":
            {
                var sql = CliInputHelpers.TryGetString(tool.Options, "sql");
                var dbPath = _services.GetRequiredService<IOptions<IndexStoreOptions>>().Value.DatabasePath;
                return await QueryCommandExecutor.ExecuteAsync(sql ?? string.Empty, dbPath, cancellationToken).ConfigureAwait(false);
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
            default:
                return ToolExecutionResult.FromError($"Unknown command: {tool.Name}");
        }
    }

    internal static bool SupportsCommand(string commandName)
        => SupportedCommands.Contains(commandName);

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
