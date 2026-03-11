using CallGraph.Contracts;

namespace CallGraph.Core.Indexing;

public interface IIndexJobStore
{
    IndexJobStatusResponse CreateJob(string solutionId, string status, string? message = null);
    bool TryGetJob(string jobId, out IndexJobStatusResponse job);
    void UpdateJob(IndexJobStatusResponse job);
}
