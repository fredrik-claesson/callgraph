namespace CallGraph.Core.Solutions;

public interface ISolutionLoader
{
    Task<SolutionLoadContext> LoadAsync(string solutionPath, bool slnOnly, CancellationToken cancellationToken);
    Task<SolutionLoadContext> LoadProjectAsync(string projectPath, CancellationToken cancellationToken);
}
