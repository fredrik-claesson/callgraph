using CallGraph.Core.Projects;
using Microsoft.CodeAnalysis.MSBuild;

namespace CallGraph.Core.Solutions;

public sealed class SolutionLoader : ISolutionLoader
{
    private readonly ISolutionFileParser _solutionFileParser;

    public SolutionLoader(IProjectFilter projectFilter, ISolutionFileParser solutionFileParser)
    {
        _ = projectFilter;
        _solutionFileParser = solutionFileParser;
    }

    public async Task<SolutionLoadContext> LoadAsync(
        string solutionPath,
        bool slnOnly,
        CancellationToken cancellationToken)
    {
        var normalizedSolutionPath = Path.GetFullPath(solutionPath);

        // Configure MSBuild properties to enable analyzer loading
        var properties = new Dictionary<string, string>
        {
            // Enable loading analyzer assemblies from NuGet packages
            ["ResolveAssemblyReferenceIgnoreTargetFrameworkAttributeVersionMismatch"] = "true"
        };

        var workspace = MSBuildWorkspace.Create(properties);
        try
        {
            var solution = await workspace
                .OpenSolutionAsync(normalizedSolutionPath, progress: null, cancellationToken)
                .ConfigureAwait(false);

            var projects = solution.Projects.ToList();

            if (slnOnly)
            {
                var slnProjects = _solutionFileParser.ReadProjectPaths(normalizedSolutionPath);
                projects = projects
                    .Where(p => p.FilePath is not null &&
                                slnProjects.Contains(Path.GetFullPath(p.FilePath)))
                    .ToList();
            }

            return new SolutionLoadContext(workspace, projects);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public async Task<SolutionLoadContext> LoadProjectAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        var normalizedProjectPath = Path.GetFullPath(projectPath);

        // Configure MSBuild properties to enable analyzer loading
        var properties = new Dictionary<string, string>
        {
            // Enable loading analyzer assemblies from NuGet packages
            ["ResolveAssemblyReferenceIgnoreTargetFrameworkAttributeVersionMismatch"] = "true"
        };

        var workspace = MSBuildWorkspace.Create(properties);
        try
        {
            var project = await workspace
                .OpenProjectAsync(normalizedProjectPath, progress: null, cancellationToken)
                .ConfigureAwait(false);

            var projects = new List<Microsoft.CodeAnalysis.Project> { project };

            return new SolutionLoadContext(workspace, projects);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }
}
