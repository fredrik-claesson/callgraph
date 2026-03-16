using System.Collections.Concurrent;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Indexing;

namespace CallGraph.Tests;

public sealed class GraphBuilderDepthTests
{
    [Fact]
    public void ExternalDepthOne_ExcludesSecondClassHopButKeepsSameClassUpstream()
    {
        var session = CreateSession(
            ("Comm.Target", "CommunicationComponent"),
            ("Gateway.DirectCaller", "GatewayComponent"),
            ("Gateway.UpstreamSameClass", "GatewayComponent"),
            ("Router.TooFar", "RouterComponent"),
            ("Router.TooFar2", "RouterComponent"));

        Connect(session, "Gateway.DirectCaller", "Comm.Target");
        Connect(session, "Gateway.UpstreamSameClass", "Gateway.DirectCaller");
        Connect(session, "Router.TooFar", "Gateway.UpstreamSameClass");
        Connect(session, "Router.TooFar2", "Router.TooFar");

        var graph = new GraphBuilder().BuildGraph(
            session,
            targets: ["Comm.Target"],
            depth: 1,
            direction: "inbound",
            visibility: "external");

        AssertContainsEdge(graph, "Gateway.DirectCaller", "Comm.Target");
        AssertContainsEdge(graph, "Gateway.UpstreamSameClass", "Gateway.DirectCaller");
        AssertDoesNotContainEdge(graph, "Router.TooFar", "Gateway.UpstreamSameClass");
        AssertDoesNotContainNode(graph, "Router.TooFar");
        AssertDoesNotContainNode(graph, "Router.TooFar2");
    }

    [Fact]
    public void InternalDepthOne_ExcludesSecondMethodHop()
    {
        var session = CreateSession(
            ("A.Target", "ClassA"),
            ("B.Direct", "ClassB"),
            ("C.Upstream", "ClassC"));

        Connect(session, "B.Direct", "A.Target");
        Connect(session, "C.Upstream", "B.Direct");

        var graph = new GraphBuilder().BuildGraph(
            session,
            targets: ["A.Target"],
            depth: 1,
            direction: "inbound",
            visibility: "internal");

        AssertContainsEdge(graph, "B.Direct", "A.Target");
        AssertDoesNotContainEdge(graph, "C.Upstream", "B.Direct");
        AssertDoesNotContainNode(graph, "C.Upstream");
    }

    private static IndexSession CreateSession(params (string Id, string Type)[] methods)
    {
        var nodes = new ConcurrentDictionary<string, Node>(StringComparer.Ordinal);
        var outbound = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var edges = new List<Edge>();

        foreach (var method in methods)
        {
            nodes[method.Id] = new Node
            {
                Id = method.Id,
                Kind = "method",
                Display = method.Id,
                ContainingType = method.Type
            };
            outbound[method.Id] = new HashSet<string>(StringComparer.Ordinal);
        }

        return new IndexSession(nodes, outbound, edges, []);
    }

    private static void Connect(IndexSession session, string from, string to)
    {
        var calls = session.Outbound.GetOrAdd(from, _ => new HashSet<string>(StringComparer.Ordinal));
        calls.Add(to);
        session.Edges.Add(new Edge
        {
            From = from,
            To = to,
            Direction = "outbound",
            Kind = "calls-direct"
        });
    }

    private static void AssertContainsEdge(Graph graph, string from, string to)
        => Assert.Contains(graph.Edges, e => e.From == from && e.To == to);

    private static void AssertDoesNotContainEdge(Graph graph, string from, string to)
        => Assert.DoesNotContain(graph.Edges, e => e.From == from && e.To == to);

    private static void AssertDoesNotContainNode(Graph graph, string id)
        => Assert.DoesNotContain(graph.Nodes, n => n.Id == id);
}
