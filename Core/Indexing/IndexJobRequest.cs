namespace CallGraph.Core.Indexing;

public sealed record IndexJobRequest(
    string JobId,
    string SolutionId,
    string SolutionPath,
    bool SlnOnly,
    bool IsReindex);
