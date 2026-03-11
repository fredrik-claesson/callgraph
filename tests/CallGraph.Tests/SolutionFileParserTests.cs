
using CallGraph.Core.Solutions;

namespace CallGraph.Tests;

public sealed class SolutionFileParserTests
{
    [Fact]
    public void ReadsProjectPathsFromSolution_WithWindowsStyleSeparators()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);

        var slnPath = Path.Combine(root, "App.sln");
        var project1 = Path.Combine(root, "src", "App", "App.csproj");
        var project2 = Path.Combine(root, "tests", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(project2)!);

        File.WriteAllText(slnPath, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App.Tests", "tests\App.Tests.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);

        var parser = new SolutionFileParser();
        var results = parser.ReadProjectPaths(slnPath);

        Assert.Contains(Path.GetFullPath(project1), results);
        Assert.Contains(Path.GetFullPath(project2), results);
    }

    [Fact]
    public void ReadsProjectPathsFromSolution_WithUnixStyleSeparators()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);

        var slnPath = Path.Combine(root, "App.sln");
        var project1 = Path.Combine(root, "src", "App", "App.csproj");
        var project2 = Path.Combine(root, "tests", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(project2)!);

        File.WriteAllText(slnPath, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src/App/App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App.Tests", "tests/App.Tests.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);

        var parser = new SolutionFileParser();
        var results = parser.ReadProjectPaths(slnPath);

        Assert.Contains(Path.GetFullPath(project1), results);
        Assert.Contains(Path.GetFullPath(project2), results);
    }
}
