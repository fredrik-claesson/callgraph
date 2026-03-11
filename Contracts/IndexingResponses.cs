namespace CallGraph.Contracts;

public sealed record IndexJobResponse(string JobId, string SolutionId);

public sealed record IndexJobStatusResponse(
    string JobId,
    string SolutionId,
    string Status,
    string? Message = null);
