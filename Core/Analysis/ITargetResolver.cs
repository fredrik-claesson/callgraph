namespace CallGraph.Core.Analysis;

public interface ITargetResolver
{
    Task<HashSet<string>> ResolveTargetsAsync(
        string solutionPath,
        bool slnOnly,
        string filePath,
        string? methodName,
        CancellationToken cancellationToken);
}
