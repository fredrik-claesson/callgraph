namespace CallGraph.Core.Git;

public interface IGitRepositoryInspector
{
    Task<GitRepositoryInfo?> TryGetRepositoryInfoAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitPathChange>> GetCommitChangesAsync(
        string repositoryRoot,
        string fromCommit,
        string toCommit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GitPathChange>> GetPendingChangesAsync(
        string repositoryRoot,
        CancellationToken cancellationToken);
}
