using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using CallGraph.Core.Solutions;

namespace CallGraph.Core.Analysis;

public interface IGraphBuilder
{
    SolutionIndex BuildIndex(
        string solutionId,
        string solutionPath,
        IndexSession session,
        bool slnOnly,
        DateTime? indexedAtUtc = null);
    Graph BuildGraph(IndexSession session, HashSet<string> targets, int depth, string direction, string visibility);
}
