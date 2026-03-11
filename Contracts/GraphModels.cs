namespace CallGraph.Contracts;

public sealed class Graph
{
    public int Version { get; set; }
    public List<string> Targets { get; set; } = new();
    public List<Node> Nodes { get; set; } = new();
    public List<Edge> Edges { get; set; } = new();
}

public sealed class Node
{
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public string? Display { get; set; }
    public string? ContainingType { get; set; }
    public string? FilePath { get; set; }
    public int? StartLine { get; set; }
    public string? Accessibility { get; set; }
}

public sealed class Edge
{
    public required string From { get; set; }
    public required string To { get; set; }
    public required string Direction { get; set; }
    public required string Kind { get; set; }
}
