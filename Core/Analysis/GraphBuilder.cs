using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using CallGraph.Core.Solutions;

namespace CallGraph.Core.Analysis;

public sealed class GraphBuilder : IGraphBuilder
{
    public SolutionIndex BuildIndex(
        string solutionId,
        string solutionPath,
        IndexSession session,
        bool slnOnly,
        DateTime? indexedAtUtc = null)
        => new()
        {
            SolutionId = solutionId,
            SolutionPath = Path.GetFullPath(solutionPath),
            IndexedAtUtc = indexedAtUtc ?? DateTime.UtcNow,
            SlnOnly = slnOnly,
            Nodes = session.Nodes.Values
                .DistinctBy(n => n.Id)
                .OrderBy(n => n.Id, StringComparer.Ordinal)
                .ToList(),
            Edges = session.Edges
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => e.Direction, StringComparer.Ordinal)
                .ThenBy(e => e.Kind, StringComparer.Ordinal)
                .ToList(),
            ProjectPaths = session.ProjectPaths
        };

    public Graph BuildGraph(IndexSession session, HashSet<string> targets, int depth, string direction, string visibility)
    {
        var edges = new List<Edge>();
        var included = new HashSet<string>(targets, StringComparer.Ordinal);
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var outbound = BuildAdjacency(session.Edges, outbound: true);
        var inbound = BuildAdjacency(session.Edges, outbound: false);
        var includeOutbound = string.Equals(direction, "outbound", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(direction, "bi-directional", StringComparison.OrdinalIgnoreCase);
        var includeInbound = string.Equals(direction, "inbound", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(direction, "bi-directional", StringComparison.OrdinalIgnoreCase);
        var useClassBasedDepth = string.Equals(visibility, "external", StringComparison.OrdinalIgnoreCase);

        void AddEdge(TraversalEdge edge)
        {
            var edgeKey = $"{edge.Output.From}\u0000{edge.Output.To}\u0000{edge.Output.Direction}\u0000{edge.Output.Kind}";
            if (edgeKeys.Add(edgeKey))
                edges.Add(edge.Output);
            included.Add(edge.Output.From);
            included.Add(edge.Output.To);
        }

        IEnumerable<TraversalEdge> OutNext(string k) => outbound.TryGetValue(k, out var s) ? s : Array.Empty<TraversalEdge>();
        IEnumerable<TraversalEdge> InNext(string k) => inbound.TryGetValue(k, out var s) ? s : Array.Empty<TraversalEdge>();

        if (useClassBasedDepth)
        {
            if (includeOutbound)
                TraverseByClass(targets, depth, session.Nodes, OutNext, AddEdge);
            if (includeInbound)
                TraverseByClass(targets, depth, session.Nodes, InNext, AddEdge);
        }
        else
        {
            if (includeOutbound)
                TraverseByMethod(targets, depth, OutNext, AddEdge);
            if (includeInbound)
                TraverseByMethod(targets, depth, InNext, AddEdge);
        }

        var outNodes = included
            .Select(id => session.Nodes.TryGetValue(id, out var n) ? n : new Node { Id = id, Kind = "method", Display = id })
            .DistinctBy(n => n.Id)
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .ToList();

        var orderedTargets = targets.OrderBy(t => t, StringComparer.Ordinal).ToList();

        return new Graph
        {
            Version = 1,
            Targets = orderedTargets,
            Nodes = outNodes,
            Edges = edges
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => e.Direction, StringComparer.Ordinal)
                .ThenBy(e => e.Kind, StringComparer.Ordinal)
                .ToList()
        };
    }

    private static Dictionary<string, List<TraversalEdge>> BuildAdjacency(
        IEnumerable<Edge> edges,
        bool outbound)
    {
        var adjacency = new Dictionary<string, List<TraversalEdge>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            var key = outbound ? edge.From : edge.To;
            var next = outbound ? edge.To : edge.From;
            var output = new Edge
            {
                From = edge.From,
                To = edge.To,
                Direction = outbound ? "outbound" : "inbound",
                Kind = edge.Kind
            };

            if (!adjacency.TryGetValue(key, out var list))
            {
                list = new List<TraversalEdge>();
                adjacency[key] = list;
            }

            list.Add(new TraversalEdge(next, output));
        }

        return adjacency;
    }

    private static void TraverseByClass(
        IEnumerable<string> starts,
        int maxClassDepth,
        IReadOnlyDictionary<string, Node> nodes,
        Func<string, IEnumerable<TraversalEdge>> next,
        Action<TraversalEdge> onEdge)
    {
        var distances = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new LinkedList<string>();

        foreach (var start in starts)
        {
            if (distances.TryAdd(start, 0))
                queue.AddLast(start);
        }

        while (queue.Count > 0)
        {
            var fromMethod = queue.First!.Value;
            queue.RemoveFirst();

            if (!distances.TryGetValue(fromMethod, out var currentClassDepth))
                continue;

            var fromClass = GetContainingType(fromMethod, nodes);
            foreach (var edge in next(fromMethod))
            {
                var toMethod = edge.Next;
                var toClass = GetContainingType(toMethod, nodes);

                var weight = string.Equals(fromClass, toClass, StringComparison.Ordinal) ? 0 : 1;
                var newClassDepth = currentClassDepth + weight;
                if (newClassDepth > maxClassDepth)
                    continue;

                onEdge(edge);

                if (!distances.TryGetValue(toMethod, out var existingDepth) || newClassDepth < existingDepth)
                {
                    distances[toMethod] = newClassDepth;
                    if (weight == 0)
                        queue.AddFirst(toMethod);
                    else
                        queue.AddLast(toMethod);
                }
            }
        }
    }

    private static void TraverseByMethod(
        IEnumerable<string> starts,
        int maxDepth,
        Func<string, IEnumerable<TraversalEdge>> next,
        Action<TraversalEdge> onEdge)
    {
        var seenDepth = new Dictionary<string, int>(StringComparer.Ordinal);
        var frontier = new Queue<(string MethodId, int Depth)>();

        foreach (var start in starts)
        {
            if (seenDepth.TryAdd(start, 0))
                frontier.Enqueue((start, 0));
        }

        while (frontier.Count > 0)
        {
            var (fromMethod, currentDepth) = frontier.Dequeue();
            foreach (var edge in next(fromMethod))
            {
                var toMethod = edge.Next;
                var newDepth = currentDepth + 1;
                if (newDepth > maxDepth)
                    continue;

                onEdge(edge);

                if (!seenDepth.TryGetValue(toMethod, out _))
                {
                    seenDepth[toMethod] = newDepth;
                    frontier.Enqueue((toMethod, newDepth));
                }
            }
        }
    }

    private static string GetContainingType(string methodId, IReadOnlyDictionary<string, Node> nodes)
    {
        // Try to get from node metadata first
        if (nodes.TryGetValue(methodId, out var node) && !string.IsNullOrWhiteSpace(node.ContainingType))
            return node.ContainingType!;
        
        // Fallback: extract from method ID (format: M:Namespace.Class.Method(...))
        // Find the last dot before the opening parenthesis or end of string
        var colon = methodId.IndexOf(':');
        if (colon < 0)
            return methodId;
        
        var afterColon = methodId.Substring(colon + 1);
        var paren = afterColon.IndexOf('(');
        var beforeParen = paren >= 0 ? afterColon.Substring(0, paren) : afterColon;
        
        var lastDot = beforeParen.LastIndexOf('.');
        if (lastDot < 0)
            return beforeParen;
        
        return beforeParen.Substring(0, lastDot);
    }

    private sealed record TraversalEdge(string Next, Edge Output);
}
