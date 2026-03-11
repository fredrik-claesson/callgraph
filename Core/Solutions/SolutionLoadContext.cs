using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Solutions;

public sealed class SolutionLoadContext : IAsyncDisposable
{
    public SolutionLoadContext(Workspace workspace, IReadOnlyList<Project> projects)
    {
        Workspace = workspace;
        Projects = projects;
    }

    public Workspace Workspace { get; }
    public IReadOnlyList<Project> Projects { get; }

    public ValueTask DisposeAsync()
    {
        Workspace.Dispose();
        return ValueTask.CompletedTask;
    }
}
