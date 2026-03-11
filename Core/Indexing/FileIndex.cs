using CallGraph.Contracts;

namespace CallGraph.Core.Indexing;

public sealed class FileIndex
{
    public required string FilePath { get; init; }
    public List<Node> Nodes { get; init; } = new();
    public List<Edge> Edges { get; init; } = new();
}
