using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Projects;

public sealed class ProjectFilter : IProjectFilter
{
    public bool IsTestProject(Project project)
    {
        if (project.Name.Contains("test", StringComparison.OrdinalIgnoreCase))
            return true;

        var path = project.FilePath ?? "";
        if (path.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (project.FilePath is not null && File.Exists(project.FilePath))
        {
            var txt = File.ReadAllText(project.FilePath);
            if (txt.Contains("IsTestProject", StringComparison.OrdinalIgnoreCase) &&
                txt.Contains("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (txt.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                txt.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
                txt.Contains("mstest", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
