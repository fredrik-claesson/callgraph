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

            var loadedProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var projectPath in selectedProjectPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncLoadedProjectPaths(workspace, loadedProjectPaths);
                if (loadedProjectPaths.Contains(projectPath))
                    continue;

                try
                {
                    await workspace.OpenProjectAsync(projectPath, progress: null, cancellationToken).ConfigureAwait(false);
                }
                catch (ArgumentException) when (IsProjectLoaded(workspace, projectPath))
                {
                    // Some project graphs are already materialized by prior OpenProjectAsync calls.
                    // If this path is already present in the workspace, skip duplicate-open failures.
                }
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

    private static bool IsProjectLoaded(MSBuildWorkspace workspace, string projectPath)
    {
        var normalizedProjectPath = Path.GetFullPath(projectPath);
        return workspace.CurrentSolution.Projects.Any(project =>
            project.FilePath is not null &&
            string.Equals(Path.GetFullPath(project.FilePath), normalizedProjectPath, StringComparison.OrdinalIgnoreCase));
    }

    private static void SyncLoadedProjectPaths(MSBuildWorkspace workspace, ISet<string> loadedProjectPaths)
    {
        foreach (var existingProjectPath in workspace.CurrentSolution.Projects
                     .Select(project => project.FilePath)
                     .Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            loadedProjectPaths.Add(Path.GetFullPath(existingProjectPath!));
        }
    }
}
