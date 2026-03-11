namespace CallGraph.Core.Indexing;

public interface IIndexJobQueue
{
    ValueTask EnqueueAsync(IndexJobRequest request, CancellationToken cancellationToken);
    ValueTask<IndexJobRequest> DequeueAsync(CancellationToken cancellationToken);
}
