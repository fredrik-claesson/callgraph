using CallGraph.Contracts;

namespace CallGraph.Core.Output;

public static class ToolResponseMapper
{
    public static SearchFileToolResponse ToSearchFileResponse(IReadOnlyList<SearchFileMatch> matches)
    {
        var rows = matches
            .Select(static match => new SearchFileToolRow(match.FilePath))
            .ToList();

        return new SearchFileToolResponse(rows.Count, rows);
    }

    public static SearchMethodToolResponse ToSearchMethodResponse(IReadOnlyList<SearchMethodMatch> matches)
    {
        var rows = matches
            .Select(static match => new SearchMethodToolRow(
                ExtractMethodName(match.Method.Display, match.Method.Id),
                match.Method.Display,
                match.Method.FilePath,
                match.Method.StartLine,
                match.Method.ContainingType))
            .ToList();

        return new SearchMethodToolResponse(rows.Count, rows);
    }

    public static DiagnosticToolResponse ToDiagnosticResponse(
        IReadOnlyList<Diagnostic> diagnostics,
        int totalCount)
    {
        var rows = diagnostics
            .Select(static diagnostic => new DiagnosticToolRow(
                diagnostic.Id,
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.FilePath,
                diagnostic.StartLine,
                diagnostic.StartColumn,
                diagnostic.EndLine,
                diagnostic.EndColumn))
            .ToList();

        return new DiagnosticToolResponse(
            totalCount,
            rows);
    }

    public static AnalyzeToolResponse ToAnalyzeResponse(Graph graph)
    {
        var methodIds = graph.Nodes
            .Select(static node => node.Id)
            .Concat(graph.Edges.Select(static edge => edge.From))
            .Concat(graph.Edges.Select(static edge => edge.To))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToList();

        var methodIdMap = methodIds
            .Select(static (id, index) => new { id, shortId = $"m{index + 1}" })
            .ToDictionary(static x => x.id, static x => x.shortId, StringComparer.Ordinal);

        var methods = graph.Nodes
            .Select(node => new AnalyzeMethodToolRow(
                methodIdMap[node.Id],
                ExtractMethodName(node.Display, node.Id),
                node.ContainingType,
                node.FilePath,
                node.StartLine))
            .OrderBy(static method => method.MethodId, StringComparer.Ordinal)
            .ToList();

        var calls = graph.Edges
            .Select(edge => new AnalyzeCallToolRow(
                methodIdMap[edge.From],
                methodIdMap[edge.To],
                edge.Direction))
            .OrderBy(static call => call.CallerMethodId, StringComparer.Ordinal)
            .ThenBy(static call => call.CalleeMethodId, StringComparer.Ordinal)
            .ThenBy(static call => call.Direction, StringComparer.Ordinal)
            .ToList();

        return new AnalyzeToolResponse(methods, calls);
    }

    private static string ExtractMethodName(string? display, string methodId)
    {
        if (!string.IsNullOrWhiteSpace(display))
        {
            var candidate = display.Trim();
            var parenIndex = candidate.IndexOf('(');
            if (parenIndex > 0)
                candidate = candidate[..parenIndex];

            var spaceIndex = candidate.LastIndexOf(' ');
            if (spaceIndex >= 0 && spaceIndex < candidate.Length - 1)
                candidate = candidate[(spaceIndex + 1)..];

            var dotIndex = candidate.LastIndexOf('.');
            if (dotIndex >= 0 && dotIndex < candidate.Length - 1)
                candidate = candidate[(dotIndex + 1)..];

            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();
        }

        var fallback = methodId;
        var assemblySeparatorIndex = fallback.IndexOf(':');
        if (assemblySeparatorIndex >= 0 && assemblySeparatorIndex < fallback.Length - 1)
            fallback = fallback[(assemblySeparatorIndex + 1)..];

        var fallbackParenIndex = fallback.IndexOf('(');
        if (fallbackParenIndex > 0)
            fallback = fallback[..fallbackParenIndex];

        var fallbackDotIndex = fallback.LastIndexOf('.');
        if (fallbackDotIndex >= 0 && fallbackDotIndex < fallback.Length - 1)
            fallback = fallback[(fallbackDotIndex + 1)..];

        return fallback.Trim();
    }
}
