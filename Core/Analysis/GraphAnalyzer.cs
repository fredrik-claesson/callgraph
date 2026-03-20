using System.Collections.Concurrent;
using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using CallGraph.Core.Solutions;

namespace CallGraph.Core.Analysis;

public sealed class GraphAnalyzer : IGraphAnalyzer
{
    private readonly IIndexStore _indexStore;
    private readonly ITargetResolver _targetResolver;
    private readonly IGraphBuilder _graphBuilder;

    public GraphAnalyzer(
        IIndexStore indexStore,
        ITargetResolver targetResolver,
        IGraphBuilder graphBuilder)
    {
        _indexStore = indexStore;
        _targetResolver = targetResolver;
        _graphBuilder = graphBuilder;
    }

    public async Task<AnalyzeResult> AnalyzeAsync(AnalyzeRequest request, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAnalyzeContextAsync(request, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
            return new AnalyzeResult(null, resolution.Error);

        var resolvedRequest = resolution.Request!;
        var (solutionPath, slnOnly) = resolution.Selection!;
        var index = await _indexStore.LoadAsync(solutionPath, cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            return new AnalyzeResult(
                null,
                new AnalyzeError(AnalyzeErrorKind.IndexNotReady, "Index missing or in progress."));
        }

        var targets = ResolveTargetsFromIndex(index, resolvedRequest.FilePath, resolvedRequest.Method);
        if (targets.Count == 0)
        {
            targets = await _targetResolver
                .ResolveTargetsAsync(solutionPath, slnOnly, resolvedRequest.FilePath, resolvedRequest.Method, cancellationToken)
                .ConfigureAwait(false);
        }

        if (targets.Count == 0)
        {
            return new AnalyzeResult(
                null,
                new AnalyzeError(AnalyzeErrorKind.TargetsNotFound, "No targets matched the request."));
        }

        var session = BuildSession(index);
        var depth = resolvedRequest.Depth ?? 2;
        var direction = resolvedRequest.Direction ?? "bi-directional";
        var visibility = NormalizeVisibility(resolvedRequest.Visibility);

        // Visibility affects depth counting strategy, not edge filtering:
        // - external: class-based depth (only increments when crossing class boundaries)
        // - internal: method-based depth (every hop counts)
        // Both modes traverse ALL edges including private/internal calls.

        var graph = _graphBuilder.BuildGraph(session, targets, depth, direction, visibility);

        return new AnalyzeResult(graph, null);
    }

    private async Task<(AnalyzeRequest? Request, SolutionSelection? Selection, AnalyzeError? Error)> ResolveAnalyzeContextAsync(
        AnalyzeRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedFilePath = NormalizeFilePath(request.FilePath);
        var hasAbsoluteFilePath = Path.IsPathRooted(request.FilePath);

        if (!string.IsNullOrWhiteSpace(request.SolutionPath))
        {
            var normalizedSolutionPath = Path.GetFullPath(request.SolutionPath);
            var info = await _indexStore
                .GetSolutionByPathAsync(normalizedSolutionPath, cancellationToken)
                .ConfigureAwait(false);
            if (info is null)
            {
                return (null, null, new AnalyzeError(AnalyzeErrorKind.IndexNotReady, "Solution is not indexed."));
            }

            if (!hasAbsoluteFilePath)
            {
                var resolved = await ResolveFilePathForSolutionAsync(info, normalizedFilePath, cancellationToken)
                    .ConfigureAwait(false);
                if (resolved is null)
                {
                    return (null, null,
                        new AnalyzeError(AnalyzeErrorKind.IndexNotReady, "No indexed solutions contain this file."));
                }

                request = request with { FilePath = resolved };
            }
            else
            {
                request = request with { FilePath = Path.GetFullPath(request.FilePath) };
            }

            return (request, new SolutionSelection(info.SolutionPath, info.SlnOnly), null);
        }

        if (!string.IsNullOrWhiteSpace(request.SolutionId))
        {
            var info = await _indexStore
                .GetSolutionByIdAsync(request.SolutionId, cancellationToken)
                .ConfigureAwait(false);
            if (info is null)
            {
                return (null, null, new AnalyzeError(AnalyzeErrorKind.IndexNotReady, "Solution is not indexed."));
            }

            if (!hasAbsoluteFilePath)
            {
                var resolved = await ResolveFilePathForSolutionAsync(info, normalizedFilePath, cancellationToken)
                    .ConfigureAwait(false);
                if (resolved is null)
                {
                    return (null, null,
                        new AnalyzeError(AnalyzeErrorKind.IndexNotReady, "No indexed solutions contain this file."));
                }

                request = request with { FilePath = resolved };
            }
            else
            {
                request = request with { FilePath = Path.GetFullPath(request.FilePath) };
            }

            return (request, new SolutionSelection(info.SolutionPath, info.SlnOnly), null);
        }

        if (hasAbsoluteFilePath)
        {
            var normalized = Path.GetFullPath(request.FilePath);
            var matches = await _indexStore
                .FindSolutionsByFilePathAsync(normalized, cancellationToken)
                .ConfigureAwait(false);

            if (matches.Count == 0)
            {
                var singleIndexed = await GetSingleIndexedSolutionAsync(cancellationToken).ConfigureAwait(false);
                if (singleIndexed is not null)
                {
                    request = request with { FilePath = normalized };
                    return (request, new SolutionSelection(singleIndexed.SolutionPath, singleIndexed.SlnOnly), null);
                }

                return (null, null,
                    new AnalyzeError(AnalyzeErrorKind.IndexNotReady, "No indexed solutions contain this file."));
            }

            if (matches.Count > 1)
            {
                return (null, null, new AnalyzeError(
                    AnalyzeErrorKind.AmbiguousSolution,
                    "Multiple indexed solutions contain this file.",
                    matches));
            }

            var match = matches[0];
            request = request with { FilePath = normalized };
            return (request, new SolutionSelection(match.SolutionPath, match.SlnOnly), null);
        }

        var relativeMatches = await FindRelativeMatchesAsync(normalizedFilePath, cancellationToken).ConfigureAwait(false);

        if (relativeMatches.Count == 0)
        {
            var singleIndexed = await GetSingleIndexedSolutionAsync(cancellationToken).ConfigureAwait(false);
            if (singleIndexed is not null)
            {
                var resolved = await ResolveFilePathForSolutionAsync(singleIndexed, normalizedFilePath, cancellationToken)
                    .ConfigureAwait(false);
                if (resolved is not null)
                {
                    request = request with { FilePath = resolved };
                    return (request, new SolutionSelection(singleIndexed.SolutionPath, singleIndexed.SlnOnly), null);
                }
            }

            return (null, null,
                new AnalyzeError(AnalyzeErrorKind.IndexNotReady, "No indexed solutions contain this file."));
        }

        var distinctSolutions = relativeMatches
            .Select(match => match.Solution)
            .DistinctBy(solution => solution.SolutionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctSolutions.Count > 1)
        {
            return (null, null, new AnalyzeError(
                AnalyzeErrorKind.AmbiguousSolution,
                "Multiple indexed solutions contain this file.",
                distinctSolutions));
        }

        var resolvedMatch = relativeMatches
            .OrderBy(match => match.FilePath, StringComparer.OrdinalIgnoreCase)
            .First();
        request = request with { FilePath = resolvedMatch.FilePath };
        var selection = new SolutionSelection(resolvedMatch.Solution.SolutionPath, resolvedMatch.Solution.SlnOnly);
        return (request, selection, null);
    }

    private static IndexSession BuildSession(SolutionIndex index)
    {
        var nodes = new ConcurrentDictionary<string, Node>(StringComparer.Ordinal);
        foreach (var node in index.Nodes)
            nodes[node.Id] = node;

        var outbound = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edge in index.Edges)
        {
            var set = outbound.GetOrAdd(edge.From, _ => new HashSet<string>(StringComparer.Ordinal));
            set.Add(edge.To);
        }

        return new IndexSession(nodes, outbound, index.Edges.ToList(), new List<string>());
    }

    private static string NormalizeVisibility(string? visibility)
    {
        if (string.IsNullOrWhiteSpace(visibility))
            return "internal";

        var trimmed = visibility.Trim();
        if (string.Equals(trimmed, "external", StringComparison.OrdinalIgnoreCase))
            return "external";

        if (string.Equals(trimmed, "internal", StringComparison.OrdinalIgnoreCase))
            return "internal";

        return "internal";
    }

    private static HashSet<string> ResolveTargetsFromIndex(
        SolutionIndex index,
        string filePath,
        string? methodName)
    {
        var normalizedFilePath = Path.GetFullPath(filePath);
        var candidates = index.Nodes
            .Where(node =>
                !string.IsNullOrWhiteSpace(node.FilePath) &&
                string.Equals(Path.GetFullPath(node.FilePath!), normalizedFilePath, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(methodName))
        {
            candidates = candidates.Where(node =>
            {
                var name = ExtractMethodName(node);
                if (string.IsNullOrWhiteSpace(name))
                    return false;
                if (node.ContainingType is not null &&
                    string.Equals(name, node.ContainingType, StringComparison.Ordinal))
                    return false;
                return string.Equals(name, methodName, StringComparison.Ordinal);
            });
        }

        return candidates
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? ExtractMethodName(Node node)
    {
        if (!string.IsNullOrWhiteSpace(node.Display))
            return ExtractMethodNameFromDisplay(node.Display!);

        return string.IsNullOrWhiteSpace(node.Id) ? null : ExtractMethodNameFromKey(node.Id);
    }

    private static string ExtractMethodNameFromDisplay(string display)
    {
        var paren = display.IndexOf('(');
        var trimmed = (paren >= 0 ? display[..paren] : display).Trim();
        var dot = trimmed.LastIndexOf('.');
        return dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
    }

    private static string ExtractMethodNameFromKey(string key)
    {
        var colon = key.IndexOf(':');
        var candidate = colon >= 0 ? key[(colon + 1)..] : key;
        var paren = candidate.IndexOf('(');
        if (paren >= 0)
            candidate = candidate[..paren];

        var dot = candidate.LastIndexOf('.');
        return dot >= 0 ? candidate[(dot + 1)..] : candidate;
    }

    private static string NormalizeFilePath(string filePath)
    {
        var normalized = filePath.Trim();
        normalized = normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.TrimStart(Path.DirectorySeparatorChar);
    }

    private async Task<IReadOnlyList<SolutionFileMatch>> FindRelativeMatchesAsync(
        string relativeFilePath,
        CancellationToken cancellationToken)
    {
        var matches = await _indexStore
            .FindSolutionsByFilePathSuffixAsync(relativeFilePath, cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count > 0)
            return matches;

        var alternate = TrySwapDirectorySeparators(relativeFilePath);
        if (alternate is null)
            return matches;

        return await _indexStore
            .FindSolutionsByFilePathSuffixAsync(alternate, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? TrySwapDirectorySeparators(string path)
    {
        if (Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar)
            return null;

        var hasPrimary = path.IndexOf(Path.DirectorySeparatorChar) >= 0;
        var hasAlt = path.IndexOf(Path.AltDirectorySeparatorChar) >= 0;
        if (!hasPrimary && !hasAlt)
            return null;

        if (hasPrimary && hasAlt)
            return null;

        var from = hasPrimary ? Path.DirectorySeparatorChar : Path.AltDirectorySeparatorChar;
        var to = hasPrimary ? Path.AltDirectorySeparatorChar : Path.DirectorySeparatorChar;
        return path.Replace(from, to);
    }

    private async Task<string?> ResolveFilePathForSolutionAsync(
        SolutionInfo solution,
        string relativeFilePath,
        CancellationToken cancellationToken)
    {
        var matches = await FindRelativeMatchesAsync(relativeFilePath, cancellationToken).ConfigureAwait(false);

        var match = matches
            .Where(candidate => string.Equals(
                candidate.Solution.SolutionId,
                solution.SolutionId,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return match?.FilePath;
    }

    private sealed record SolutionSelection(string SolutionPath, bool SlnOnly);

    private async Task<SolutionInfo?> GetSingleIndexedSolutionAsync(CancellationToken cancellationToken)
    {
        var solutions = await _indexStore.ListSolutionsAsync(cancellationToken).ConfigureAwait(false);
        return solutions.Count == 1 ? solutions[0] : null;
    }
}
