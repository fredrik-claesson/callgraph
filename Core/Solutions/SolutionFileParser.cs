namespace CallGraph.Core.Solutions;

public sealed class SolutionFileParser : ISolutionFileParser
{
    public HashSet<string> ReadProjectPaths(string solutionPath)
    {
        var slnDir = Path.GetDirectoryName(solutionPath) ?? "";
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in File.ReadLines(solutionPath))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("Project(", StringComparison.Ordinal))
                continue;

            var firstComma = trimmed.IndexOf(',');
            if (firstComma < 0)
                continue;

            var secondComma = trimmed.IndexOf(',', firstComma + 1);
            if (secondComma < 0)
                continue;

            var pathSegment = trimmed.Substring(firstComma + 1, secondComma - firstComma - 1).Trim();
            if (pathSegment.Length < 2 || pathSegment[0] != '"' || pathSegment[^1] != '"')
                continue;

            var relPath = pathSegment[1..^1];
            if (!relPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;

            // .sln project paths commonly use backslashes even on non-Windows.
            // Normalize separators before combining so paths match Roslyn project file paths.
            var normalizedRelPath = relPath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

            var fullPath = Path.GetFullPath(Path.Combine(slnDir, normalizedRelPath));
            projects.Add(fullPath);
        }

        return projects;
    }
}
