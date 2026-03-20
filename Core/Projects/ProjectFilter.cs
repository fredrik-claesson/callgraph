using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Projects;

public sealed class ProjectFilter : IProjectFilter
{
    public bool IsTestProject(Project project)
    {
        return TestProjectClassifier.IsTestProject(project.Name, project.FilePath);
    }
}
