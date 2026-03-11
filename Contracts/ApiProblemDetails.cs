namespace CallGraph.Contracts;

public sealed class ApiProblemDetails
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public int? Status { get; init; }
    public string? Detail { get; init; }
    public string? Instance { get; init; }
    public IDictionary<string, object?> Extensions { get; init; } = new Dictionary<string, object?>();
}
