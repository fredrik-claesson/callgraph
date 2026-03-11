using CallGraph.Core.Analysis;
using CallGraph.Core.Solutions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CallGraph.Tests;

public sealed class TargetResolverTests
{
    [Fact]
    public async Task ResolvesTargetsByMethodName()
    {
        var (resolver, filePath) = CreateResolver("""
            namespace Sample;
            public class Widget
            {
                public Widget() { }
                public void Foo() { }
                public void Bar() { }
            }
            """);

        var targets = await resolver.ResolveTargetsAsync(
            solutionPath: "ignored.sln",
            slnOnly: true,
            filePath: filePath,
            methodName: "Foo",
            cancellationToken: CancellationToken.None);

        Assert.Single(targets);
    }

    [Fact]
    public async Task ResolvesAllBaseMethodsWhenMethodOmitted()
    {
        var (resolver, filePath) = CreateResolver("""
            namespace Sample;
            public class Widget
            {
                public Widget() { }
                public void Foo() { }
                public void Bar() { }
            }
            """);

        var targets = await resolver.ResolveTargetsAsync(
            solutionPath: "ignored.sln",
            slnOnly: true,
            filePath: filePath,
            methodName: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(3, targets.Count);
    }

    private static (TargetResolver Resolver, string FilePath) CreateResolver(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "Sample",
            "Sample",
            LanguageNames.CSharp);
        var project = workspace.AddProject(projectInfo);
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cs");
        var documentInfo = DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id),
            "Widget.cs",
            filePath: filePath,
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())));
        var document = workspace.AddDocument(documentInfo);

        var loader = new TestSolutionLoader(workspace, [document.Project]);
        return (new TargetResolver(loader), filePath);
    }

    private sealed class TestSolutionLoader(Workspace workspace, IReadOnlyList<Project> projects) : ISolutionLoader
    {
        public Task<SolutionLoadContext> LoadAsync(string solutionPath, bool slnOnly, CancellationToken cancellationToken)
            => Task.FromResult(new SolutionLoadContext(workspace, projects));

        public Task<SolutionLoadContext> LoadProjectAsync(string projectPath, CancellationToken cancellationToken)
            => Task.FromResult(new SolutionLoadContext(workspace, projects));
    }
}
