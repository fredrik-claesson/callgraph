namespace CallGraph.Core.Projects;

public static class TestProjectClassifier
{
    public static bool IsTestProject(string? projectName, string? projectFilePath)
    {
        if (!string.IsNullOrWhiteSpace(projectName) &&
            projectName.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (LooksLikeTestPath(projectFilePath))
            return true;

        if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
            return false;

        string content;
        try
        {
            content = File.ReadAllText(projectFilePath);
        }
        catch
        {
            return false;
        }

        if (content.Contains("IsTestProject", StringComparison.OrdinalIgnoreCase) &&
            content.Contains("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return content.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("mstest", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeTestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(".tests.", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("-test", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("-tests", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("_test", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("_tests", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFileUnderAnyProjectDirectory(string filePath, IReadOnlyCollection<string> projectPaths)
    {
        if (string.IsNullOrWhiteSpace(filePath) || projectPaths.Count == 0)
            return false;

        var normalizedFilePath = Path.GetFullPath(filePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var projectPath in projectPaths)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrWhiteSpace(projectDirectory))
                continue;

            var normalizedProjectDirectory = Path.GetFullPath(projectDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(normalizedFilePath, normalizedProjectDirectory, StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalizedFilePath.StartsWith(
                    normalizedProjectDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
