using CallGraph.Core.Projects;
using Microsoft.CodeAnalysis;

namespace CallGraph.Tests;

public sealed class ProjectFilterTests
{
    [Fact]
    public void DetectsTestProjectByName()
    {
        using var workspace = new AdhocWorkspace();
        var csprojPath = CreateTempProject("<Project></Project>");
        var project = CreateProject(workspace, "MyTests", csprojPath);

        var filter = new ProjectFilter();

        Assert.True(filter.IsTestProject(project));
    }

    [Fact]
    public void DetectsTestProjectByPath()
    {
        using var workspace = new AdhocWorkspace();
        var csprojPath = CreateTempProject("<Project></Project>", "tests");
        var project = CreateProject(workspace, "App", csprojPath);

        var filter = new ProjectFilter();

        Assert.True(filter.IsTestProject(project));
    }

    [Fact]
    public void DetectsTestProjectByProperty()
    {
        using var workspace = new AdhocWorkspace();
        var csprojPath = CreateTempProject("<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        var project = CreateProject(workspace, "App", csprojPath);

        var filter = new ProjectFilter();

        Assert.True(filter.IsTestProject(project));
    }

    [Fact]
    public void DetectsTestProjectByReference()
    {
        using var workspace = new AdhocWorkspace();
        var csprojPath = CreateTempProject("<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
        var project = CreateProject(workspace, "App", csprojPath);

        var filter = new ProjectFilter();

        Assert.True(filter.IsTestProject(project));
    }

    [Fact]
    public void DoesNotFlagNonTestProject()
    {
        using var workspace = new AdhocWorkspace();
        var csprojPath = CreateTempProject("<Project></Project>");
        var project = CreateProject(workspace, "App", csprojPath);

        var filter = new ProjectFilter();

        Assert.False(filter.IsTestProject(project));
    }

    private static Project CreateProject(AdhocWorkspace workspace, string name, string csprojPath)
    {
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            filePath: csprojPath);
        return workspace.AddProject(projectInfo);
    }

    private static string CreateTempProject(string content, string? subdir = null)
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var directory = string.IsNullOrWhiteSpace(subdir) ? root : Path.Combine(root, subdir);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "App.csproj");
        File.WriteAllText(path, content);
        return path;
    }
}
