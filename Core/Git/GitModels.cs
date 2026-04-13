namespace CallGraph.Core.Git;

public enum GitPathChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmerged,
    Unknown,
    Untracked
}

public sealed record GitPathChange(
    string Path,
    GitPathChangeKind Kind,
    string? OldPath = null);

public sealed record GitRepositoryInfo(
    string RepositoryRoot,
    string GitCommonDirectory,
    string? HeadCommit);
