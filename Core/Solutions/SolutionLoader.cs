using CallGraph.Core.Projects;
using Microsoft.CodeAnalysis.MSBuild;

namespace CallGraph.Core.Solutions;

public sealed class SolutionLoader : ISolutionLoader
{
    private readonly IProjectFilter _projectFilter;
    private readonly ISolutionFileParser _solutionFileParser;

    public SolutionLoader(IProjectFilter projectFilter, ISolutionFileParser solutionFileParser)
    {
        _projectFilter = projectFilter;
        _solutionFileParser = solutionFileParser;
    }

    public async Task<SolutionLoadContext> LoadAsync(
        string solutionPath,
        bool slnOnly,
        CancellationToken cancellationToken)
    {
        var normalizedSolutionPath = Path.GetFullPath(solutionPath);

        if (slnOnly)
        {
            var slnProjectPaths = _solutionFileParser.ReadProjectPaths(normalizedSolutionPath)
                .Select(Path.GetFullPath)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (slnProjectPaths.Count > 0)
                return await LoadSelectedProjectsAsync(slnProjectPaths, cancellationToken).ConfigureAwait(false);
        }

        var workspace = MSBuildWorkspace.Create(CreateWorkspaceProperties());
        try
        {
            var solution = await workspace
                .OpenSolutionAsync(normalizedSolutionPath, progress: null, cancellationToken)
                .ConfigureAwait(false);

            var projects = solution.Projects
                .Where(p => !_projectFilter.IsTestProject(p))
                .ToList();

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

        var workspace = MSBuildWorkspace.Create(CreateWorkspaceProperties());
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

    private async Task<SolutionLoadContext> LoadSelectedProjectsAsync(
        IReadOnlyList<string> projectPaths,
        CancellationToken cancellationToken)
    {
        var workspace = MSBuildWorkspace.Create(CreateWorkspaceProperties());
        try
        {
            var selectedProjectPaths = projectPaths
                .Where(path => !_projectFilter.IsTestProjectPath(path, Path.GetFileNameWithoutExtension(path)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedProjectPaths.Count == 0)
                return new SolutionLoadContext(workspace, []);

            foreach (var projectPath in selectedProjectPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await workspace.OpenProjectAsync(projectPath, progress: null, cancellationToken).ConfigureAwait(false);
            }

            var projects = workspace.CurrentSolution.Projects
                .Where(p => p.FilePath is not null)
                .Where(p => selectedProjectPaths.Contains(Path.GetFullPath(p.FilePath!)))
                .Where(p => !_projectFilter.IsTestProject(p))
                .ToList();

            return new SolutionLoadContext(workspace, projects);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static Dictionary<string, string> CreateWorkspaceProperties()
        => new()
        {
            // Enable loading analyzer assemblies from NuGet packages.
            ["ResolveAssemblyReferenceIgnoreTargetFrameworkAttributeVersionMismatch"] = "true"
        };
}
