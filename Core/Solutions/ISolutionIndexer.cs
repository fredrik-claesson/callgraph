using CallGraph.Contracts;

namespace CallGraph.Core.Solutions;

public interface ISolutionIndexer
{
    Task<IndexJobResponse> EnqueueIndexAsync(IndexRequest request, CancellationToken cancellationToken);
    Task<IndexJobResponse> EnqueueReindexAsync(ReindexRequest request, CancellationToken cancellationToken);
}
