using CallGraph.Contracts;

namespace CallGraph.Core.Solutions;

public sealed class SolutionIndex
{
    public required string SolutionId { get; init; }
    public required string SolutionPath { get; init; }
    public DateTime IndexedAtUtc { get; init; }
    public bool SlnOnly { get; init; }
    public List<Node> Nodes { get; init; } = new();
    public List<Edge> Edges { get; init; } = new();
    public List<string> ProjectPaths { get; init; } = new();
}
