using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Projects;

public interface IProjectFilter
{
    bool IsTestProject(Project project);
}
