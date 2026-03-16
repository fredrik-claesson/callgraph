using CallGraph.Contracts;

namespace CallGraph.Core.Indexing;

internal sealed record DocumentCallGraph(IReadOnlyList<Node> Nodes, IReadOnlyList<Edge> Edges);
