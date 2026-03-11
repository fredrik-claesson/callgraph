using CallGraph.Core.Indexing;

namespace CallGraph.Core.Projects;

public interface IProjectIndexer
{
    Task<IndexSession> IndexAsync(IReadOnlyList<Microsoft.CodeAnalysis.Project> projects, CancellationToken cancellationToken);
}
