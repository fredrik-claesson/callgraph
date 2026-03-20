namespace CallGraph.Core.Indexing;

public interface IIndexJobQueue
{
    ValueTask<IndexJobQueueEnqueueResult> EnqueueAsync(IndexJobRequest request, CancellationToken cancellationToken);
    ValueTask<IndexJobRequest> DequeueAsync(CancellationToken cancellationToken);
    void MarkCompleted(IndexJobRequest request);
}

public readonly record struct IndexJobQueueEnqueueResult(
    bool Accepted,
    string ActiveJobId,
    bool ActiveJobIsReindex);
