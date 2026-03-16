using System.Collections.Concurrent;
using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Projects;

public sealed class ProjectIndexer : IProjectIndexer
{
    public async Task<IndexSession> IndexAsync(IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        var outbound = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var nodes = new ConcurrentDictionary<string, Node>(StringComparer.Ordinal);
        var edges = new ConcurrentBag<Edge>();

        var projectPaths = projects
            .Where(p => p.FilePath is not null)
            .Select(p => Path.GetFullPath(p.FilePath!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dispatchMaps = new Lazy<Task<DispatchMaps>>(
            () => DispatchMapBuilder.BuildAsync(projects, cancellationToken));

        var documents = projects
            .SelectMany(p => p.Documents)
            .Where(d => d.SupportsSyntaxTree && d.FilePath is not null)
            .ToList();

        await Parallel.ForEachAsync(documents, cancellationToken, async (doc, ct) =>
            {
                var graph = await DocumentCallGraphExtractor
                    .ExtractAsync(doc, () => dispatchMaps.Value, ct)
                    .ConfigureAwait(false);

                foreach (var node in graph.Nodes)
                    nodes[node.Id] = node;

                foreach (var edge in graph.Edges)
                {
                    edges.Add(edge);
                    var calls = outbound.GetOrAdd(edge.From, _ => new HashSet<string>(StringComparer.Ordinal));
                    lock (calls)
                    {
                        calls.Add(edge.To);
                    }
                }
            })
            .ConfigureAwait(false);

        var distinctEdges = edges
            .DistinctBy(edge => $"{edge.From}\u0000{edge.To}\u0000{edge.Kind}", StringComparer.Ordinal)
            .OrderBy(edge => edge.From, StringComparer.Ordinal)
            .ThenBy(edge => edge.To, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind, StringComparer.Ordinal)
            .ToList();

        return new IndexSession(nodes, outbound, distinctEdges, projectPaths);
    }
}
