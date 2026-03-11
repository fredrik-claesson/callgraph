using CallGraph.Contracts;
using CallGraph.Core.Solutions;

namespace CallGraph.Core.Analysis;

public sealed record AnalyzeResult(Graph? Graph, AnalyzeError? Error);

public sealed record AnalyzeError(
    AnalyzeErrorKind Kind,
    string Detail,
    IReadOnlyList<SolutionInfo>? Solutions = null);

public enum AnalyzeErrorKind
{
    IndexNotReady,
    AmbiguousSolution,
    TargetsNotFound
}
