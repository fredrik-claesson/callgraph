namespace CallGraph.Core.Watching;

public interface ISolutionWatcherRegistry
{
    Task EnsureWatchingAsync(string solutionPath, bool slnOnly, CancellationToken cancellationToken);
    Task StopWatchingAsync(string solutionPath, CancellationToken cancellationToken);
}
