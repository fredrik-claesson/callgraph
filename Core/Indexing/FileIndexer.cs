using System.Collections.Concurrent;
using System.Diagnostics;
using CallGraph.Core.Solutions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CallGraph.Core.Indexing;

public sealed class FileIndexer : IFileIndexer
{
    private readonly ISolutionLoader _solutionLoader;
    private readonly ISolutionContextCache? _solutionContextCache;
    private readonly ILogger<FileIndexer> _logger;

    public FileIndexer(
        ISolutionLoader solutionLoader,
        ISolutionContextCache? solutionContextCache = null,
        ILogger<FileIndexer>? logger = null)
    {
        _solutionLoader = solutionLoader;
        _solutionContextCache = solutionContextCache;
        _logger = logger ?? NullLogger<FileIndexer>.Instance;
    }

    public async Task<FileIndex?> IndexFileAsync(
        string solutionPath,
        string filePath,
        bool slnOnly,
        CancellationToken cancellationToken)
        => (await IndexFilesAsync(solutionPath, new[] { filePath }, slnOnly, cancellationToken)
            .ConfigureAwait(false))
            .FirstOrDefault();

    public async Task<IReadOnlyList<FileIndex>> IndexFilesAsync(
        string solutionPath,
        IReadOnlyList<string> filePaths,
        bool slnOnly,
        CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0)
            return Array.Empty<FileIndex>();

        var normalizedFilePaths = filePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalTimer = Stopwatch.StartNew();
        var stageTimer = Stopwatch.StartNew();

        var loadedContext = _solutionContextCache is not null
            ? await _solutionContextCache.GetOrLoadAsync(solutionPath, slnOnly, cancellationToken).ConfigureAwait(false)
            : await _solutionLoader.LoadAsync(solutionPath, slnOnly, cancellationToken).ConfigureAwait(false);
        try
        {
            var loadSolutionMs = stageTimer.ElapsedMilliseconds;
            stageTimer.Restart();

            var documentLookup = BuildDocumentLookup(loadedContext.Projects);
            var buildLookupMs = stageTimer.ElapsedMilliseconds;
            stageTimer.Restart();

            long dispatchMapBuildMs = -1;
            var dispatchMapBuilt = 0;
            var dispatchMaps = new Lazy<Task<DispatchMaps>>(
                async () =>
                {
                    var mapTimer = Stopwatch.StartNew();
                    var maps = await DispatchMapBuilder.BuildAsync(loadedContext.Projects, cancellationToken)
                        .ConfigureAwait(false);
                    mapTimer.Stop();
                    Interlocked.Exchange(ref dispatchMapBuildMs, mapTimer.ElapsedMilliseconds);
                    Interlocked.Exchange(ref dispatchMapBuilt, 1);
                    return maps;
                });

            var results = new ConcurrentBag<FileIndex>();
            var matchedDocumentCount = 0;
            var missingDocumentCount = 0;
            await Parallel.ForEachAsync(normalizedFilePaths, cancellationToken, async (normalizedFilePath, ct) =>
            {
                if (!documentLookup.TryGetValue(normalizedFilePath, out var doc))
                {
                    Interlocked.Increment(ref missingDocumentCount);
                    return;
                }

                Interlocked.Increment(ref matchedDocumentCount);

                var graph = await DocumentCallGraphExtractor
                    .ExtractAsync(doc, () => dispatchMaps.Value, ct)
                    .ConfigureAwait(false);

                results.Add(new FileIndex
                {
                    FilePath = normalizedFilePath,
                    Nodes = graph.Nodes.ToList(),
                    Edges = graph.Edges.ToList()
                });
            }).ConfigureAwait(false);

            var indexDocumentsMs = stageTimer.ElapsedMilliseconds;
            totalTimer.Stop();

            _logger.LogInformation(
                "File indexing timings for {SolutionPath}: requested={RequestedCount}, matched={MatchedCount}, missing={MissingCount}, loadSolution={LoadSolutionMs}ms, buildLookup={BuildLookupMs}ms, buildInterfaceMapBuilt={BuildInterfaceMapBuilt}, buildInterfaceMap={BuildInterfaceMapMs}ms, indexDocuments={IndexDocumentsMs}ms, total={TotalMs}ms, indexedResults={IndexedResultCount}.",
                solutionPath,
                normalizedFilePaths.Count,
                matchedDocumentCount,
                missingDocumentCount,
                loadSolutionMs,
                buildLookupMs,
                dispatchMapBuilt == 1,
                dispatchMapBuilt == 1 ? dispatchMapBuildMs : 0,
                indexDocumentsMs,
                totalTimer.ElapsedMilliseconds,
                results.Count);

            return results.ToList();
        }
        finally
        {
            if (_solutionContextCache is null)
                await loadedContext.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Dictionary<string, Document> BuildDocumentLookup(IEnumerable<Project> projects)
    {
        var lookup = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            foreach (var doc in project.Documents)
            {
                if (!doc.SupportsSyntaxTree || doc.FilePath is null)
                    continue;

                var normalized = Path.GetFullPath(doc.FilePath);
                lookup[normalized] = doc;
            }
        }

        return lookup;
    }
}
