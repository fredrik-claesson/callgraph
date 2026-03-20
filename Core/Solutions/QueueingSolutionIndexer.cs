using CallGraph.Contracts;
using CallGraph.Core.Indexing;

namespace CallGraph.Core.Solutions;

public sealed class QueueingSolutionIndexer : ISolutionIndexer
{
    private readonly IIndexJobStore _jobStore;
    private readonly IIndexJobQueue _queue;

    public QueueingSolutionIndexer(IIndexJobStore jobStore, IIndexJobQueue queue)
    {
        _jobStore = jobStore;
        _queue = queue;
    }

    public async Task<IndexJobResponse> EnqueueIndexAsync(IndexRequest request, CancellationToken cancellationToken)
        => await EnqueueAsync(request.SolutionPath, request.SlnOnly, isReindex: false, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IndexJobResponse> EnqueueReindexAsync(ReindexRequest request, CancellationToken cancellationToken)
        => await EnqueueAsync(request.SolutionPath, request.SlnOnly, isReindex: true, cancellationToken)
            .ConfigureAwait(false);

    private async Task<IndexJobResponse> EnqueueAsync(
        string solutionPath,
        bool slnOnly,
        bool isReindex,
        CancellationToken cancellationToken)
    {
        var solutionId = SolutionIdentity.FromPath(solutionPath);
        var job = _jobStore.CreateJob(solutionId, "Queued");
        var request = new IndexJobRequest(job.JobId, solutionId, solutionPath, slnOnly, isReindex);
        var enqueue = await _queue.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
        if (enqueue.Accepted)
            return new IndexJobResponse(job.JobId, solutionId);

        _jobStore.UpdateJob(new IndexJobStatusResponse(
            job.JobId,
            solutionId,
            "Superseded",
            $"Merged into active job {enqueue.ActiveJobId}."));
        return new IndexJobResponse(enqueue.ActiveJobId, solutionId);
    }
}
