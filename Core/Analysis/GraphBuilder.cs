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
    {
        var edges = new List<Edge>();

        foreach (var (caller, callees) in session.Outbound)
        {
            foreach (var callee in callees)
            {
                edges.Add(new Edge
                {
                    From = caller,
                    To = callee,
                    Direction = "outbound",
                    Kind = "calls"
                });
            }
        }

        return new SolutionIndex
        {
            SolutionId = solutionId,
            SolutionPath = Path.GetFullPath(solutionPath),
            IndexedAtUtc = indexedAtUtc ?? DateTime.UtcNow,
            SlnOnly = slnOnly,
            Nodes = session.Nodes.Values
                .DistinctBy(n => n.Id)
                .OrderBy(n => n.Id, StringComparer.Ordinal)
                .ToList(),
            Edges = edges
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => e.Direction, StringComparer.Ordinal)
                .ToList(),
            ProjectPaths = session.ProjectPaths
        };
    }

    public Graph BuildGraph(IndexSession session, HashSet<string> targets, int depth, string direction, string visibility)
    {
        var edges = new List<Edge>();
        var included = new HashSet<string>(targets, StringComparer.Ordinal);
        var inbound = Invert(session.Outbound);
        var includeOutbound = string.Equals(direction, "outbound", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(direction, "bi-directional", StringComparison.OrdinalIgnoreCase);
        var includeInbound = string.Equals(direction, "inbound", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(direction, "bi-directional", StringComparison.OrdinalIgnoreCase);
        var useClassBasedDepth = string.Equals(visibility, "external", StringComparison.OrdinalIgnoreCase);

        void AddEdge(string from, string to, string dir)
        {
            edges.Add(new Edge { From = from, To = to, Direction = dir, Kind = "calls" });
            included.Add(from);
            included.Add(to);
        }

        IEnumerable<string> OutNext(string k) => session.Outbound.TryGetValue(k, out var s) ? s : Array.Empty<string>();
        IEnumerable<string> InNext(string k) => inbound.TryGetValue(k, out var s) ? s : Array.Empty<string>();

        foreach (var t in targets)
        {
            if (useClassBasedDepth)
            {
                // External: depth counts class boundaries only, all edges traversed
                if (includeOutbound)
                    TraverseByClass(t, depth, session.Nodes, OutNext, (from, to) => AddEdge(from, to, "outbound"));
                if (includeInbound)
                    TraverseByClass(t, depth, session.Nodes, InNext, (from, caller) => AddEdge(caller, from, "inbound"));
            }
            else
            {
                // Internal: every hop counts toward depth
                if (includeOutbound)
                    TraverseByMethod(t, depth, OutNext, (from, to) => AddEdge(from, to, "outbound"));
                if (includeInbound)
                    TraverseByMethod(t, depth, InNext, (from, caller) => AddEdge(caller, from, "inbound"));
            }
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
                .ToList()
        };
    }

    private static Dictionary<string, HashSet<string>> Invert(
        IDictionary<string, HashSet<string>> outbound)
    {
        var inbound = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (caller, callees) in outbound)
        {
            foreach (var callee in callees)
            {
                if (!inbound.TryGetValue(callee, out var callers))
                {
                    inbound[callee] = callers = new HashSet<string>(StringComparer.Ordinal);
                }
                callers.Add(caller);
            }
        }
        return inbound;
    }

    private static void TraverseByClass(
        string start,
        int maxClassDepth,
        IReadOnlyDictionary<string, Node> nodes,
        Func<string, IEnumerable<string>> next,
        Action<string, string> onEdge)
    {
        var seenDepth = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [start] = 0
        };

        // Frontier tracks (methodId, classDepth)
        var frontier = new List<(string MethodId, int ClassDepth)> { (start, 0) };

        while (frontier.Count > 0)
        {
            var nextFrontier = new List<(string MethodId, int ClassDepth)>();

            foreach (var (fromMethod, currentClassDepth) in frontier)
            {
                var fromClass = GetContainingType(fromMethod, nodes);

                foreach (var toMethod in next(fromMethod))
                {
                    var toClass = GetContainingType(toMethod, nodes);

                    // Only increment depth when crossing to a different class
                    var newClassDepth = string.Equals(fromClass, toClass, StringComparison.Ordinal)
                        ? currentClassDepth
                        : currentClassDepth + 1;

                    if (seenDepth.TryGetValue(toMethod, out var existingDepth))
                    {
                        if (existingDepth <= maxClassDepth)
                            onEdge(fromMethod, toMethod);
                        continue;
                    }

                    if (newClassDepth > maxClassDepth)
                        continue;

                    onEdge(fromMethod, toMethod);
                    seenDepth[toMethod] = newClassDepth;
                    nextFrontier.Add((toMethod, newClassDepth));
                }
            }

            frontier = nextFrontier;
        }
    }

    private static void TraverseByMethod(
        string start,
        int maxDepth,
        Func<string, IEnumerable<string>> next,
        Action<string, string> onEdge)
    {
        var seenDepth = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [start] = 0
        };

        // Frontier tracks (methodId, depth) - every hop counts
        var frontier = new List<(string MethodId, int Depth)> { (start, 0) };

        while (frontier.Count > 0)
        {
            var nextFrontier = new List<(string MethodId, int Depth)>();

            foreach (var (fromMethod, currentDepth) in frontier)
            {
                foreach (var toMethod in next(fromMethod))
                {
                    var newDepth = currentDepth + 1;

                    if (seenDepth.TryGetValue(toMethod, out var existingDepth))
                    {
                        if (existingDepth <= maxDepth)
                            onEdge(fromMethod, toMethod);
                        continue;
                    }

                    if (newDepth > maxDepth)
                        continue;

                    onEdge(fromMethod, toMethod);
                    seenDepth[toMethod] = newDepth;
                    nextFrontier.Add((toMethod, newDepth));
                }
            }

            frontier = nextFrontier;
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
}
