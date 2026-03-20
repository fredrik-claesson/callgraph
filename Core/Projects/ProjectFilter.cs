using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Projects;

public sealed class ProjectFilter : IProjectFilter
{
    public bool IsTestProject(Project project)
    {
        if (IsNameOrPathTestProject(project.Name, project.FilePath))
            return true;

        return IsTestProjectFile(project.FilePath);
    }

    public bool IsTestProjectPath(string projectPath, string? projectName = null)
        => IsNameOrPathTestProject(projectName ?? string.Empty, projectPath) || IsTestProjectFile(projectPath);

    private static bool IsNameOrPathTestProject(string name, string? path)
    {
        if (name.Contains("test", StringComparison.OrdinalIgnoreCase))
            return true;

        var projectPath = path ?? string.Empty;
        return projectPath.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               projectPath.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestProjectFile(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            return false;

        var txt = File.ReadAllText(projectPath);
        if (txt.Contains("IsTestProject", StringComparison.OrdinalIgnoreCase) &&
            txt.Contains("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return txt.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
               txt.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
               txt.Contains("mstest", StringComparison.OrdinalIgnoreCase);
    }
}
