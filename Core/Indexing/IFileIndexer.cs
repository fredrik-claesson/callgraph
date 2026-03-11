namespace CallGraph.Core.Indexing;

public interface IFileIndexer
{
    Task<FileIndex?> IndexFileAsync(string solutionPath, string filePath, bool slnOnly, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileIndex>> IndexFilesAsync(
        string solutionPath,
        IReadOnlyList<string> filePaths,
        bool slnOnly,
        CancellationToken cancellationToken);
}
