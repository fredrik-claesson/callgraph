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
    Task<DateTime?> GetIndexedAtUtcAsync(string solutionPath, CancellationToken cancellationToken);
    Task<SolutionInfo?> GetSolutionByIdAsync(string solutionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexedFileInfo>> ListFilesAsync(string solutionPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListProjectPathsAsync(string solutionPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<SolutionInfo>> FindSolutionsByFilePathAsync(string filePath, CancellationToken cancellationToken);
    Task<IReadOnlyList<SolutionFileMatch>> FindSolutionsByFilePathSuffixAsync(
        string relativeFilePath,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SolutionProjectMatch>> FindProjectsByPathSuffixAsync(
        string relativeProjectPath,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchFileMatch>> SearchFilesAsync(
        string pattern,
        bool useRegex,
        string? solutionPath,
        string? solutionId,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchMethodMatch>> SearchMethodsAsync(
        string pattern,
        bool useRegex,
        string? solutionPath,
        string? solutionId,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchMethodMatch>> ListMethodsAsync(
        string visibility,
        string? solutionPath,
        string? solutionId,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken);
    Task<Node?> GetMethodAsync(string solutionPath, string methodKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<Edge>> GetEdgesAsync(string solutionPath, string methodKey, CancellationToken cancellationToken);
    Task UpdateFileAsync(string solutionPath, FileIndex update, CancellationToken cancellationToken);
    Task RemoveFileAsync(string solutionPath, string filePath, CancellationToken cancellationToken);

    Task<string?> GetIndexedHeadCommitAsync(string solutionPath, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    Task UpdateSolutionMetadataAsync(
        string solutionPath,
        DateTime indexedAtUtc,
        string? headCommit,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
