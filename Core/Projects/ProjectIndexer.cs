using System.Collections.Concurrent;
using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace CallGraph.Core.Projects;

public sealed class ProjectIndexer : IProjectIndexer
{
    private readonly ILogger<ProjectIndexer> _logger;

    public ProjectIndexer(ILogger<ProjectIndexer>? logger = null)
    {
        _logger = logger ?? NullLogger<ProjectIndexer>.Instance;
    }

    public async Task<IndexSession> IndexAsync(IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        var totalTimer = Stopwatch.StartNew();
        var stageTimer = Stopwatch.StartNew();

        var outbound = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var nodes = new ConcurrentDictionary<string, Node>(StringComparer.Ordinal);
        var edges = new ConcurrentBag<Edge>();

        var projectPaths = projects
            .Where(p => p.FilePath is not null)
            .Select(p => Path.GetFullPath(p.FilePath!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        long dispatchMapBuildMs = -1;
        var dispatchMapBuilt = 0;
        var dispatchMaps = new Lazy<Task<DispatchMaps>>(
            async () =>
            {
                var mapTimer = Stopwatch.StartNew();
                var maps = await DispatchMapBuilder.BuildAsync(projects, cancellationToken).ConfigureAwait(false);
                mapTimer.Stop();
                Interlocked.Exchange(ref dispatchMapBuildMs, mapTimer.ElapsedMilliseconds);
                Interlocked.Exchange(ref dispatchMapBuilt, 1);
                return maps;
            });

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
        var indexDocumentsMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        var distinctEdges = edges
            .DistinctBy(edge => $"{edge.From}\u0000{edge.To}\u0000{edge.Kind}", StringComparer.Ordinal)
            .OrderBy(edge => edge.From, StringComparer.Ordinal)
            .ThenBy(edge => edge.To, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind, StringComparer.Ordinal)
            .ToList();
        var finalizeMs = stageTimer.ElapsedMilliseconds;
        totalTimer.Stop();

        _logger.LogInformation(
            "Project indexing timings: projects={ProjectCount}, documents={DocumentCount}, dispatchMapBuilt={DispatchMapBuilt}, dispatchMapBuild={DispatchMapBuildMs}ms, indexDocuments={IndexDocumentsMs}ms, finalize={FinalizeMs}ms, total={TotalMs}ms.",
            projects.Count,
            documents.Count,
            dispatchMapBuilt == 1,
            dispatchMapBuilt == 1 ? dispatchMapBuildMs : 0,
            indexDocumentsMs,
            finalizeMs,
            totalTimer.ElapsedMilliseconds);

        return new IndexSession(nodes, outbound, distinctEdges, projectPaths);
    }
}
