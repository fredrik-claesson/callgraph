using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;

namespace CallGraph.Core.Indexing;

public sealed class IndexJobRunner : BackgroundService, IHostedService
{
    private const int MaxWorkerCount = 4;
    private readonly int _workerCount = Math.Clamp(Environment.ProcessorCount / 4, 1, MaxWorkerCount);
    private readonly ILogger<IndexJobRunner> _logger;
    private readonly IIndexJobQueue _queue;
    private readonly IIndexingPipeline _pipeline;
    private readonly IIndexJobStore _jobStore;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _solutionLocks = new(StringComparer.OrdinalIgnoreCase);

    public IndexJobRunner(
        ILogger<IndexJobRunner> logger,
        IIndexJobQueue queue,
        IIndexingPipeline pipeline,
        IIndexJobStore jobStore)
    {
        _logger = logger;
        _queue = queue;
        _pipeline = pipeline;
        _jobStore = jobStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Index job runner started with {WorkerCount} workers.", _workerCount);
        var workers = Enumerable
            .Range(0, _workerCount)
            .Select(workerId => RunWorkerAsync(workerId, stoppingToken))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
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

            await ProcessJobAsync(workerId, request, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessJobAsync(int workerId, IndexJobRequest request, CancellationToken cancellationToken)
    {
        var lockKey = BuildSolutionKey(request);
        var solutionLock = _solutionLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        var lockAcquired = false;

        try
        {
            await solutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockAcquired = true;

            _logger.LogDebug(
                "Worker {WorkerId} processing index job {JobId} for {SolutionPath}.",
                workerId,
                request.JobId,
                request.SolutionPath);
            _jobStore.UpdateJob(new(request.JobId, request.SolutionId, "Running"));
            await _pipeline.RunAsync(request, cancellationToken).ConfigureAwait(false);
            _jobStore.UpdateJob(new(request.JobId, request.SolutionId, "Completed"));
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
        finally
        {
            if (lockAcquired)
                solutionLock.Release();

            _queue.MarkCompleted(request);
        }
    }

    private static string BuildSolutionKey(IndexJobRequest request)
        => $"{Path.GetFullPath(request.SolutionPath)}\u0000{(request.SlnOnly ? '1' : '0')}";
}
