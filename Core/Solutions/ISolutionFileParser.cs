namespace CallGraph.Core.Solutions;

public interface ISolutionFileParser
{
    HashSet<string> ReadProjectPaths(string solutionPath);
}
