using CallGraph.Contracts;

namespace CallGraph.Core.Analysis;

public interface IGraphAnalyzer
{
    Task<AnalyzeResult> AnalyzeAsync(AnalyzeRequest request, CancellationToken cancellationToken);
}
