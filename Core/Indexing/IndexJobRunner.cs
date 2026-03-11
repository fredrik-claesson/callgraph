using CallGraph.Core.Watching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace CallGraph.Core.Indexing;

public sealed class IndexJobRunner : BackgroundService, IHostedService
{
    private readonly ILogger<IndexJobRunner> _logger;
    private readonly IIndexJobQueue _queue;
    private readonly IIndexingPipeline _pipeline;
    private readonly IIndexJobStore _jobStore;
    private readonly ISolutionWatcherRegistry _watcherRegistry;

    public IndexJobRunner(
        ILogger<IndexJobRunner> logger,
        IIndexJobQueue queue,
        IIndexingPipeline pipeline,
        IIndexJobStore jobStore,
        ISolutionWatcherRegistry watcherRegistry)
    {
        _logger = logger;
        _queue = queue;
        _pipeline = pipeline;
        _jobStore = jobStore;
        _watcherRegistry = watcherRegistry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Index job runner started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            IndexJobRequest request;
            try
            {
                request = await _queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await ProcessJobAsync(request, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessJobAsync(IndexJobRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _jobStore.UpdateJob(new(request.JobId, request.SolutionId, "Running"));
            await _pipeline.RunAsync(request, cancellationToken).ConfigureAwait(false);
            _jobStore.UpdateJob(new(request.JobId, request.SolutionId, "Completed"));
            await _watcherRegistry
                .EnsureWatchingAsync(request.SolutionPath, request.SlnOnly, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Index job {JobId} canceled.", request.JobId);
            _jobStore.UpdateJob(new(request.JobId, request.SolutionId, "Canceled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Index job {JobId} failed.", request.JobId);
            _jobStore.UpdateJob(new(request.JobId, request.SolutionId, "Failed", ex.Message));
        }
    }
}
