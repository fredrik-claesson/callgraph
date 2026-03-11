namespace CallGraph.Core.Indexing;

public interface IIndexingPipeline
{
    Task RunAsync(IndexJobRequest request, CancellationToken cancellationToken);
}
