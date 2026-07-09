using System.Diagnostics;
using CallGraph.Core.Analysis;
using CallGraph.Core.Git;
using CallGraph.Core.Projects;
using CallGraph.Core.Solutions;
using Microsoft.Extensions.Logging;

namespace CallGraph.Core.Indexing;

public sealed class IndexingPipeline : IIndexingPipeline
{
    private readonly ISolutionLoader _solutionLoader;
    private readonly IProjectIndexer _projectIndexer;
    private readonly IGraphBuilder _graphBuilder;
    private readonly IIndexStore _indexStore;
    private readonly IGitRepositoryInspector _gitRepositoryInspector;
    private readonly ILogger<IndexingPipeline> _logger;

    public IndexingPipeline(
        ISolutionLoader solutionLoader,
        IProjectIndexer projectIndexer,
        IGraphBuilder graphBuilder,
        IIndexStore indexStore,
        ILogger<IndexingPipeline> logger)
        : this(
            solutionLoader,
            projectIndexer,
            graphBuilder,
            indexStore,
            new GitRepositoryInspector(),
            logger)
    {
    }

    public IndexingPipeline(
        ISolutionLoader solutionLoader,
        IProjectIndexer projectIndexer,
        IGraphBuilder graphBuilder,
        IIndexStore indexStore,
        IGitRepositoryInspector gitRepositoryInspector,
        ILogger<IndexingPipeline> logger)
    {
        _solutionLoader = solutionLoader;
        _projectIndexer = projectIndexer;
        _graphBuilder = graphBuilder;
        _indexStore = indexStore;
        _gitRepositoryInspector = gitRepositoryInspector;
        _logger = logger;
    }

    // Both index and reindex run the same full pipeline. SaveAsync replaces the target
    // solution's rows in a single transaction, so a reindex is a scoped clear-and-index
    // and is consistently faster than the previous incremental path, which paid the full
    // MSBuild workspace load anyway plus per-file diff/DB overhead.
    public async Task RunAsync(IndexJobRequest request, CancellationToken cancellationToken)
    {
        var totalTimer = Stopwatch.StartNew();
        var stageTimer = Stopwatch.StartNew();

        var gitInfo = await _gitRepositoryInspector
            .TryGetRepositoryInfoAsync(request.SolutionPath, cancellationToken)
            .ConfigureAwait(false);

        await using var context = await _solutionLoader
            .LoadAsync(request.SolutionPath, request.SlnOnly, cancellationToken)
            .ConfigureAwait(false);
        var loadMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        var session = await _projectIndexer.IndexAsync(context.Projects, cancellationToken).ConfigureAwait(false);
        var indexMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        var index = _graphBuilder.BuildIndex(request.SolutionId, request.SolutionPath, session, request.SlnOnly);
        index.HeadCommit = gitInfo?.HeadCommit;
        var buildMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        await _indexStore.SaveAsync(index, cancellationToken).ConfigureAwait(false);
        var saveMs = stageTimer.ElapsedMilliseconds;
        totalTimer.Stop();

        _logger.LogInformation(
            "Indexing timings for {SolutionPath}: load={LoadMs}ms, index={IndexMs}ms, build={BuildMs}ms, save={SaveMs}ms, total={TotalMs}ms.",
            request.SolutionPath,
            loadMs,
            indexMs,
            buildMs,
            saveMs,
            totalTimer.ElapsedMilliseconds);
    }
}
