namespace CallGraph.Core.Extraction;

public interface IMethodSourceExtractor
{
    Task<MethodSourceExtractionResult> ExtractAsync(MethodSourceExtractionRequest request, CancellationToken cancellationToken);
}
