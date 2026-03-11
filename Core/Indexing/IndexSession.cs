using System.Collections.Concurrent;
using CallGraph.Contracts;

namespace CallGraph.Core.Indexing;

public sealed class IndexSession
{
    public IndexSession(
        ConcurrentDictionary<string, Node> nodes,
        ConcurrentDictionary<string, HashSet<string>> outbound,
        List<string> projectPaths)
    {
        Nodes = nodes;
        Outbound = outbound;
        ProjectPaths = projectPaths;
    }

    public ConcurrentDictionary<string, Node> Nodes { get; }
    public ConcurrentDictionary<string, HashSet<string>> Outbound { get; }
    public List<string> ProjectPaths { get; }
}
