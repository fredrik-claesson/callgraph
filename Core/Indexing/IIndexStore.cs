using CallGraph.Contracts;
using CallGraph.Core.Solutions;

namespace CallGraph.Core.Indexing;

public interface IIndexStore
{
    Task ClearAsync(CancellationToken cancellationToken);
    Task SaveAsync(SolutionIndex index, CancellationToken cancellationToken);
    Task<SolutionIndex?> LoadAsync(string solutionPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<SolutionInfo>> ListSolutionsAsync(CancellationToken cancellationToken);
    Task<SolutionInfo?> GetSolutionByPathAsync(string solutionPath, CancellationToken cancellationToken);
    Task<SolutionInfo?> GetSolutionByIdAsync(string solutionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SolutionInfo>> FindSolutionsByFilePathAsync(string filePath, CancellationToken cancellationToken);
    Task<IReadOnlyList<SolutionFileMatch>> FindSolutionsByFilePathSuffixAsync(
        string relativeFilePath,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SolutionProjectMatch>> FindProjectsByPathSuffixAsync(
        string relativeProjectPath,
        CancellationToken cancellationToken);
    Task<Node?> GetMethodAsync(string solutionPath, string methodKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<Edge>> GetEdgesAsync(string solutionPath, string methodKey, CancellationToken cancellationToken);
}
