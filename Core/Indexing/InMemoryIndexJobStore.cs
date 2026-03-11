using System.Collections.Concurrent;
using CallGraph.Contracts;

namespace CallGraph.Core.Indexing;

public sealed class InMemoryIndexJobStore : IIndexJobStore
{
    private readonly ConcurrentDictionary<string, IndexJobStatusResponse> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public IndexJobStatusResponse CreateJob(string solutionId, string status, string? message = null)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var job = new IndexJobStatusResponse(jobId, solutionId, status, message);
        _jobs[jobId] = job;
        return job;
    }

    public bool TryGetJob(string jobId, out IndexJobStatusResponse job)
        => _jobs.TryGetValue(jobId, out job!);

    public void UpdateJob(IndexJobStatusResponse job)
        => _jobs[job.JobId] = job;
}
