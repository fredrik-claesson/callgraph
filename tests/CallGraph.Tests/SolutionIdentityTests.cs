using System.Diagnostics;
using CallGraph.Core.Solutions;

namespace CallGraph.Tests;

public sealed class SolutionIdentityTests
{
    [Fact]
    public void GeneratesStableHashForEquivalentPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        var absolutePath = Path.Combine(root, "sample.sln");
        var relativePath = Path.GetRelativePath(root, absolutePath);

        try
        {
            var relativeId = SolutionIdentity.FromPath(relativePath, root);
            var absoluteId = SolutionIdentity.FromPath(absolutePath, root);

            Assert.Equal(absoluteId, relativeId);
            Assert.Matches("^[0-9a-f]{64}$", absoluteId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void UsesGitLineageIdentityAcrossWorktrees()
    {
        var root = Path.Combine(Path.GetTempPath(), $"callgraph-identity-{Guid.NewGuid():N}");
        var repoDir = Path.Combine(root, "repo");
        var worktreeDir = Path.Combine(root, "repo-worktree");

        Directory.CreateDirectory(repoDir);

        try
        {
            RunGit(repoDir, "init");
            RunGit(repoDir, "config", "user.email", "callgraph-tests@example.com");
            RunGit(repoDir, "config", "user.name", "CallGraph Tests");

            File.WriteAllText(Path.Combine(repoDir, "sample.sln"), "Microsoft Visual Studio Solution File");
            RunGit(repoDir, "add", ".");
            RunGit(repoDir, "commit", "-m", "init");
            RunGit(repoDir, "branch", "feature");
            RunGit(repoDir, "worktree", "add", worktreeDir, "feature");

            var mainPath = Path.Combine(repoDir, "sample.sln");
            var secondaryPath = Path.Combine(worktreeDir, "sample.sln");

            var mainId = SolutionIdentity.FromPath(mainPath, slnOnly: true);
            var secondaryId = SolutionIdentity.FromPath(secondaryPath, slnOnly: true);
            var fullSolutionId = SolutionIdentity.FromPath(mainPath, slnOnly: false);
            var mainCommon = RunGitWithOutput(repoDir, "rev-parse", "--git-common-dir");
            var secondaryCommon = RunGitWithOutput(worktreeDir, "rev-parse", "--git-common-dir");
            var mainTop = RunGitWithOutput(repoDir, "rev-parse", "--show-toplevel");
            var secondaryTop = RunGitWithOutput(worktreeDir, "rev-parse", "--show-toplevel");

            Assert.True(
                string.Equals(mainId, secondaryId, StringComparison.Ordinal),
                $"Expected same identity across worktrees. mainId={mainId}, secondaryId={secondaryId}, mainCommon={mainCommon}, secondaryCommon={secondaryCommon}, mainTop={mainTop}, secondaryTop={secondaryTop}");
            Assert.NotEqual(mainId, fullSolutionId);
        }
        finally
        {
            try
            {
                if (Directory.Exists(worktreeDir))
                    RunGit(repoDir, "worktree", "remove", "--force", worktreeDir);
            }
            catch
            {
                // Best-effort cleanup for test temp repos.
            }

            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"git {string.Join(" ", args)} failed. stdout: {stdout}\nstderr: {stderr}");
    }

    private static string RunGitWithOutput(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(" ", args)} failed. stdout: {stdout}\nstderr: {stderr}");
        return stdout.Trim();
    }
}
